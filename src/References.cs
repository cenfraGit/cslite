using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace CsLite;

/// <summary>
/// Finds the assemblies a compilation needs, without asking MSBuild.
/// </summary>
/// <remarks>
/// Framework assemblies come from the SDK reference pack (or, failing that, the
/// running runtime's own directory). NuGet assemblies are read out of
/// obj/project.assets.json, which "dotnet restore" already wrote for us.
/// <para>
/// Nothing here ever loads an assembly into this process. Roslyn maps the file
/// and reads its metadata, so a source generator DLL is never locked and your
/// next build of that generator will not fail.
/// </para>
/// </remarks>
internal static class References
{
    public static IReadOnlyList<MetadataReference> ForProject(string projectDirectory)
    {
        // Keyed by simple assembly name: two references with the same identity
        // make Roslyn emit CS1703 and poison every file in the project.
        var byName = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);

        // Framework first, so a package that happens to ship a same-named
        // assembly cannot displace the one the runtime will actually load.
        foreach (var path in FrameworkAssemblies()) Add(byName, path);
        foreach (var path in NuGetAssemblies(projectDirectory)) Add(byName, path);

        Log.Debug($"resolved {byName.Count} references for {projectDirectory}");
        return byName.Values.ToList();
    }

    private static void Add(Dictionary<string, MetadataReference> map, string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (map.ContainsKey(name)) return;

        try
        {
            map[name] = MetadataReference.CreateFromFile(path);
        }
        catch (Exception error)
        {
            // Native images and stray unmanaged DLLs land here. Skipping one
            // reference is always better than failing to load the project.
            Log.Debug($"skipping reference {path}: {error.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Framework
    // -----------------------------------------------------------------------

    private static IEnumerable<string> FrameworkAssemblies()
    {
        var referencePack = FindReferencePack();
        if (referencePack is not null)
        {
            Log.Debug($"framework reference pack: {referencePack}");
            return Directory.EnumerateFiles(referencePack, "*.dll");
        }

        // Reference packs only ship with the SDK. On a runtime-only machine we
        // fall back to the implementation assemblies we are running on.
        var runtime = RuntimeEnvironment.GetRuntimeDirectory();
        Log.Debug($"no reference pack found, falling back to runtime at {runtime}");
        return Directory.EnumerateFiles(runtime, "*.dll")
            .Where(path => !Path.GetFileName(path).EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindReferencePack()
    {
        var dotnetRoot = FindDotnetRoot();
        if (dotnetRoot is null) return null;

        var packs = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
        if (!Directory.Exists(packs)) return null;

        // Highest installed version, then the highest target framework inside it.
        foreach (var version in Directory.EnumerateDirectories(packs).OrderByDescending(NumericVersionOf))
        {
            var reference = Path.Combine(version, "ref");
            if (!Directory.Exists(reference)) continue;

            var framework = Directory.EnumerateDirectories(reference)
                .OrderByDescending(NumericVersionOf)
                .FirstOrDefault();

            if (framework is not null) return framework;
        }

        return null;
    }

    private static string? FindDotnetRoot()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(fromEnvironment) && Directory.Exists(fromEnvironment))
            return fromEnvironment;

        // .../shared/Microsoft.NETCore.App/<version>/  ->  three levels up.
        var directory = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var root = directory.Parent?.Parent?.Parent;
        return root?.Exists == true ? root.FullName : null;
    }

    /// <summary>Orders "net10.0" above "net8.0", and "10.0.11" above "8.0.30".</summary>
    private static Version NumericVersionOf(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith("net", StringComparison.OrdinalIgnoreCase)) name = name[3..];

        var digits = new string(name.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return Version.TryParse(digits, out var version) ? version : new Version(0, 0);
    }

    // -----------------------------------------------------------------------
    // NuGet
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads compile-time package assemblies straight out of the restore
    /// artifact. If the project was never restored we simply find nothing, and
    /// the user sees unresolved-type errors that a "dotnet restore" fixes.
    /// </summary>
    private static IEnumerable<string> NuGetAssemblies(string projectDirectory)
    {
        var assets = Path.Combine(projectDirectory, "obj", "project.assets.json");
        if (!File.Exists(assets)) yield break;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(assets));
        }
        catch (Exception error)
        {
            Log.Warn($"could not read {assets}: {error.Message}");
            yield break;
        }

        using (document)
        {
            var root = document.RootElement;

            var packageFolders = root.TryGetProperty("packageFolders", out var folders)
                ? folders.EnumerateObject().Select(folder => folder.Name).ToList()
                : [];

            if (packageFolders.Count == 0 || !root.TryGetProperty("targets", out var targets))
                yield break;

            // Where each package unpacks on disk, keyed by "Name/Version".
            var libraryPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("libraries", out var libraries))
            {
                foreach (var library in libraries.EnumerateObject())
                {
                    if (library.Value.TryGetProperty("path", out var path))
                        libraryPaths[library.Name] = path.GetString() ?? "";
                }
            }

            foreach (var target in targets.EnumerateObject())
            {
                foreach (var package in target.Value.EnumerateObject())
                {
                    if (!package.Value.TryGetProperty("compile", out var compile)) continue;
                    if (!libraryPaths.TryGetValue(package.Name, out var relativePath)) continue;

                    foreach (var item in compile.EnumerateObject())
                    {
                        // "_._" is the NuGet marker for "compatible, but nothing to reference".
                        if (item.Name.EndsWith("_._", StringComparison.Ordinal)) continue;

                        var relative = item.Name.Replace('/', Path.DirectorySeparatorChar);

                        foreach (var folder in packageFolders)
                        {
                            var full = Path.GetFullPath(Path.Combine(folder, relativePath, relative));
                            if (File.Exists(full))
                            {
                                yield return full;
                                break;
                            }
                        }
                    }
                }
            }
        }
    }
}

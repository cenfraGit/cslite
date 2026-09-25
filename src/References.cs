using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace CsLite;

/// <summary>
/// Finds the assemblies a compilation needs, without asking MSBuild.
/// </summary>
/// <remarks>
/// Framework assemblies come from the SDK reference packs (or, failing that,
/// the running runtime's own directory). NuGet assemblies are read out of
/// obj/project.assets.json, which "dotnet restore" already wrote for us.
/// <para>
/// Nothing here ever loads an assembly into this process. Roslyn maps the file
/// and reads its metadata, so a source generator DLL is never locked and your
/// next build of that generator will not fail.
/// </para>
/// </remarks>
internal static class References
{
    /// <summary>The framework every project compiles against, whatever else it asks for.</summary>
    private const string BaseFramework = "Microsoft.NETCore.App";

    private const string DesktopFramework = "Microsoft.WindowsDesktop.App";

    /// <summary>
    /// Everything a project compiles against: its shared frameworks, then its
    /// NuGet packages.
    /// </summary>
    /// <param name="declaredFrameworks">
    /// Shared frameworks named by the csproj itself. Used alongside what the
    /// restore recorded, so a project that has never been restored still gets
    /// its framework right.
    /// </param>
    public static IReadOnlyList<MetadataReference> ForProject(
        string projectDirectory,
        string? targetFramework,
        IEnumerable<string> declaredFrameworks)
    {
        // Keyed by simple assembly name: two references with the same identity
        // make Roslyn emit CS1703 and poison every file in the project.
        var byName = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);

        // base goes last: its WindowsBase, System.Drawing and
        // Microsoft.VisualBasic are facades, and taking them over the desktop
        // pack's real ones leaves every wpf type unresolved (CS7069).
        // "Microsoft.WindowsDesktop.App.WPF" is a profile of the desktop pack.
        var frameworks = declaredFrameworks
            .Concat(FrameworkReferencesFrom(projectDirectory))
            .Select(name => name.StartsWith(DesktopFramework + ".", StringComparison.OrdinalIgnoreCase)
                ? DesktopFramework
                : name)
            .Where(name => !string.Equals(name, BaseFramework, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Append(BaseFramework)
            .ToList();

        // Framework first, so a package that happens to ship a same-named
        // assembly cannot displace the one the runtime will actually load.
        foreach (var framework in frameworks)
        {
            foreach (var path in FrameworkAssemblies(framework, targetFramework)) Add(byName, path);
        }

        foreach (var path in NuGetAssemblies(projectDirectory)) Add(byName, path);

        Log.Debug($"resolved {byName.Count} references for {projectDirectory} "
                  + $"[{string.Join(", ", frameworks)}] targeting {targetFramework ?? "unknown"}");

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
    // Shared frameworks
    // -----------------------------------------------------------------------

    /// <summary>
    /// The assemblies of one shared framework: <c>Microsoft.NETCore.App</c> for
    /// the base framework, <c>Microsoft.AspNetCore.App</c> for a web project,
    /// <c>Microsoft.WindowsDesktop.App</c> for WPF or WinForms.
    /// </summary>
    private static IEnumerable<string> FrameworkAssemblies(string framework, string? targetFramework)
    {
        var referencePack = FindReferencePack(framework + ".Ref", targetFramework);
        if (referencePack is not null)
        {
            Log.Debug($"{framework}: {referencePack}");
            return Directory.EnumerateFiles(referencePack, "*.dll");
        }

        // Reference packs only ship with the SDK. For the base framework we can
        // still fall back to the runtime we are executing on; the others have no
        // equivalent, and their absence is worth saying out loud because every
        // type in them is about to come back unresolved.
        if (!string.Equals(framework, BaseFramework, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"no reference pack for {framework}; its types will not resolve");
            return [];
        }

        var runtime = RuntimeEnvironment.GetRuntimeDirectory();
        Log.Debug($"no reference pack found, falling back to runtime at {runtime}");

        return Directory.EnumerateFiles(runtime, "*.dll")
            .Where(path => !Path.GetFileName(path).EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds the reference pack for a framework, preferring the one built for
    /// the project's own target framework.
    /// </summary>
    /// <remarks>
    /// Taking the newest installed pack regardless would let a net8.0 project
    /// compile against net10.0 reference assemblies, and the editor would then
    /// accept APIs that do not exist in the version actually being built.
    /// </remarks>
    private static string? FindReferencePack(string packName, string? targetFramework)
    {
        var dotnetRoot = FindDotnetRoot();
        if (dotnetRoot is null) return null;

        var packs = Path.Combine(dotnetRoot, "packs", packName);
        if (!Directory.Exists(packs)) return null;

        var candidates = new List<(Version Version, string Moniker, string Path)>();

        foreach (var version in SafeDirectories(packs))
        {
            var reference = Path.Combine(version, "ref");
            if (!Directory.Exists(reference)) continue;

            foreach (var moniker in SafeDirectories(reference))
                candidates.Add((NumericVersionOf(version), Path.GetFileName(moniker), moniker));
        }

        if (candidates.Count == 0) return null;

        // A "net10.0-windows" project builds against the "net10.0" pack.
        var wanted = targetFramework?.Split('-').FirstOrDefault();

        if (wanted is { Length: > 0 })
        {
            var exact = candidates
                .Where(candidate => string.Equals(candidate.Moniker, wanted, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.Version)
                .Select(candidate => candidate.Path)
                .FirstOrDefault();

            if (exact is not null) return exact;

            Log.Warn($"no {packName} for {wanted}; using the newest installed instead");
        }

        return candidates
            .OrderByDescending(candidate => NumericVersionOf(candidate.Moniker))
            .ThenByDescending(candidate => candidate.Version)
            .First()
            .Path;
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

    private static IEnumerable<string> SafeDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory);
        }
        catch (Exception error)
        {
            Log.Debug($"skipping {directory}: {error.Message}");
            return [];
        }
    }

    // -----------------------------------------------------------------------
    // NuGet
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads the shared frameworks the restore recorded. This is how a web or
    /// desktop project says it needs more than the base framework, and missing
    /// it is what makes every ASP.NET or WPF type come back unresolved.
    /// </summary>
    private static IEnumerable<string> FrameworkReferencesFrom(string projectDirectory)
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
            if (!document.RootElement.TryGetProperty("project", out var project)
                || !project.TryGetProperty("frameworks", out var frameworks))
            {
                yield break;
            }

            foreach (var framework in frameworks.EnumerateObject())
            {
                if (!framework.Value.TryGetProperty("frameworkReferences", out var references)) continue;

                foreach (var reference in references.EnumerateObject())
                    yield return reference.Name;
            }
        }
    }

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

using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CsLite;

/// <summary>A project we intend to hand to Roslyn, described in plain terms.</summary>
internal sealed record DiscoveredProject
{
    public required string Name { get; init; }
    public required string Directory { get; init; }

    /// <summary>Null for the synthetic project that collects loose .cs files.</summary>
    public string? CsprojPath { get; init; }

    public OutputKind OutputKind { get; init; } = OutputKind.DynamicallyLinkedLibrary;
    public LanguageVersion LanguageVersion { get; init; } = LanguageVersion.Preview;
    public NullableContextOptions Nullable { get; init; } = NullableContextOptions.Disable;
    public bool AllowUnsafe { get; init; }
    public IReadOnlyList<string> PreprocessorSymbols { get; init; } = [];

    /// <summary>Absolute paths of referenced .csproj files, for cross-project navigation.</summary>
    public IReadOnlyList<string> ProjectReferences { get; init; } = [];

    public List<string> SourceFiles { get; } = [];
}

/// <summary>
/// Works out the project structure by reading the directory tree and the csproj
/// XML directly. This is emphatically not an MSBuild evaluation: imports,
/// conditions, Directory.Build.props and custom targets are all ignored.
/// </summary>
/// <remarks>
/// The payoff for that shortcut is that loading is instantaneous and cannot
/// hang, deadlock, or lock a DLL. The price is that unusual build logic will be
/// invisible to us, which shows up as slightly wrong diagnostics rather than a
/// broken editor.
/// </remarks>
internal static class ProjectLoader
{
    private static readonly string[] IgnoredDirectories =
        [".git", ".vs", ".idea", "node_modules", "bin", "obj"];

    public static IReadOnlyList<DiscoveredProject> Discover(string root)
    {
        var csprojPaths = EnumerateSourceTree(root, "*.csproj").ToList();
        Log.Info($"found {csprojPaths.Count} project file(s) under {root}");

        var projects = csprojPaths
            .Select(Describe)
            .ToDictionary(project => project.Directory, project => project, PathComparer.Instance);

        // A directory with no csproj at all still deserves working navigation.
        DiscoveredProject? loose = null;

        foreach (var file in EnumerateSourceTree(root, "*.cs"))
        {
            var owner = OwnerOf(file, projects);
            if (owner is null)
            {
                loose ??= new DiscoveredProject { Name = "loose-files", Directory = root };
                loose.SourceFiles.Add(file);
            }
            else
            {
                owner.SourceFiles.Add(file);
            }
        }

        // Pick up what the last build generated: implicit global usings,
        // assembly info, and source generator output. Reading these files is
        // what lets us skip running generators without losing their types.
        foreach (var project in projects.Values)
            project.SourceFiles.AddRange(GeneratedFiles(project.Directory));

        var all = projects.Values.ToList();
        if (loose is not null && loose.SourceFiles.Count > 0) all.Add(loose);

        foreach (var project in all)
            Log.Info($"project {project.Name}: {project.SourceFiles.Count} file(s)");

        return all;
    }

    /// <summary>The owning project is the one whose directory is the longest prefix of the file.</summary>
    private static DiscoveredProject? OwnerOf(string file, Dictionary<string, DiscoveredProject> projects)
    {
        DiscoveredProject? best = null;

        foreach (var (directory, project) in projects)
        {
            if (!IsUnder(file, directory)) continue;
            if (best is null || directory.Length > best.Directory.Length) best = project;
        }

        return best;
    }

    private static bool IsUnder(string file, string directory)
    {
        var prefix = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;

        return file.StartsWith(prefix, PathComparer.Comparison);
    }

    // -----------------------------------------------------------------------
    // Reading a csproj
    // -----------------------------------------------------------------------

    private static DiscoveredProject Describe(string csprojPath)
    {
        var directory = Path.GetDirectoryName(csprojPath)!;
        var name = Path.GetFileNameWithoutExtension(csprojPath);

        XDocument document;
        try
        {
            document = XDocument.Load(csprojPath);
        }
        catch (Exception error)
        {
            Log.Warn($"could not parse {csprojPath}, using defaults: {error.Message}");
            return new DiscoveredProject { Name = name, Directory = directory, CsprojPath = csprojPath };
        }

        var properties = document.Descendants()
            .Where(element => element.Parent?.Name.LocalName == "PropertyGroup")
            .GroupBy(element => element.Name.LocalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value.Trim(), StringComparer.OrdinalIgnoreCase);

        string? Property(string key) => properties.TryGetValue(key, out var value) ? value : null;

        var languageVersion = LanguageVersion.Preview;
        if (Property("LangVersion") is { } raw && LanguageVersionFacts.TryParse(raw, out var parsed))
            languageVersion = parsed;

        // Mirror a Debug build, which is what a developer is usually looking at.
        var symbols = new List<string> { "DEBUG", "TRACE" };
        if (Property("DefineConstants") is { } defined)
        {
            symbols.AddRange(defined
                .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(symbol => !symbol.StartsWith('$')));
        }

        var projectReferences = document.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFullPath(Path.Combine(directory, include!.Replace('\\', Path.DirectorySeparatorChar))))
            .ToList();

        return new DiscoveredProject
        {
            Name = name,
            Directory = directory,
            CsprojPath = csprojPath,
            // Top-level statements only compile when the output kind is an
            // application, so getting this wrong produces a very loud CS8805.
            OutputKind = string.Equals(Property("OutputType"), "Exe", StringComparison.OrdinalIgnoreCase)
                ? OutputKind.ConsoleApplication
                : OutputKind.DynamicallyLinkedLibrary,
            LanguageVersion = languageVersion,
            Nullable = string.Equals(Property("Nullable"), "enable", StringComparison.OrdinalIgnoreCase)
                ? NullableContextOptions.Enable
                : NullableContextOptions.Disable,
            AllowUnsafe = string.Equals(Property("AllowUnsafeBlocks"), "true", StringComparison.OrdinalIgnoreCase),
            PreprocessorSymbols = symbols.Distinct(StringComparer.Ordinal).ToList(),
            ProjectReferences = projectReferences,
        };
    }

    // -----------------------------------------------------------------------
    // Walking the tree
    // -----------------------------------------------------------------------

    private static IEnumerable<string> EnumerateSourceTree(string root, string pattern)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, pattern);
            }
            catch (Exception error)
            {
                Log.Debug($"skipping {directory}: {error.Message}");
                continue;
            }

            foreach (var file in files) yield return file;

            foreach (var child in SafeSubdirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (!IgnoredDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                    pending.Push(child);
            }
        }
    }

    /// <summary>
    /// Collects build-generated sources from obj/, which we deliberately skip
    /// during the normal walk.
    /// </summary>
    /// <remarks>
    /// Only one configuration/framework folder is used. Taking them all would
    /// mean two copies of AssemblyInfo and a wall of CS0579 duplicate-attribute
    /// errors.
    /// </remarks>
    private static IEnumerable<string> GeneratedFiles(string projectDirectory)
    {
        var obj = Path.Combine(projectDirectory, "obj");
        if (!Directory.Exists(obj)) yield break;

        var outputDirectory = SafeSubdirectories(obj)
            .SelectMany(SafeSubdirectories)                                  // obj/<Config>/<Framework>
            .OrderByDescending(path => path.Contains("Debug", PathComparer.Comparison))
            .FirstOrDefault();

        if (outputDirectory is null) yield break;

        // Implicit usings and assembly attributes.
        foreach (var file in Directory.EnumerateFiles(outputDirectory, "*.g.cs"))
            yield return file;

        // Source generator output, present when the project sets
        // <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>.
        var generated = Path.Combine(outputDirectory, "generated");
        if (Directory.Exists(generated))
        {
            foreach (var file in Directory.EnumerateFiles(generated, "*.cs", SearchOption.AllDirectories))
                yield return file;
        }
    }

    private static IEnumerable<string> SafeSubdirectories(string directory)
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
}

/// <summary>
/// Path comparison that matches the filesystem: case-insensitive on Windows and
/// macOS, case-sensitive on Linux.
/// </summary>
internal sealed class PathComparer : IEqualityComparer<string>
{
    public static readonly PathComparer Instance = new();

    public static readonly StringComparison Comparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static readonly StringComparer Comparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public bool Equals(string? left, string? right) => Comparer.Equals(left, right);

    public int GetHashCode(string value) => Comparer.GetHashCode(value);
}

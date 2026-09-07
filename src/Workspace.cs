using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

namespace CsLite;

/// <summary>
/// Holds the Roslyn solution and keeps it in step with the editor.
/// </summary>
/// <remarks>
/// Everything is driven from an <see cref="AdhocWorkspace"/>, which is the
/// workspace with no project system attached. We construct the projects
/// ourselves from <see cref="ProjectLoader"/>, so nothing here can invoke a
/// build, spawn MSBuild, or load an analyzer.
/// </remarks>
internal sealed class CSharpWorkspace : IDisposable
{
    private readonly AdhocWorkspace _workspace;
    private readonly Dictionary<string, DocumentId> _documentsByPath = new(PathComparer.Instance);
    private readonly List<(string Directory, ProjectId Id)> _projectDirectories = [];

    public CSharpWorkspace()
    {
        _workspace = new AdhocWorkspace(CreateHostServices());
    }

    public Solution Solution => _workspace.CurrentSolution;

    /// <summary>Builds the whole solution once, at startup.</summary>
    public void Load(string root)
    {
        var discovered = ProjectLoader.Discover(root);

        // Two passes: every project must exist before we can wire the
        // references between them.
        var idsByCsproj = new Dictionary<string, ProjectId>(PathComparer.Instance);
        var infos = new List<(DiscoveredProject Source, ProjectInfo Info)>();

        foreach (var project in discovered)
        {
            var id = ProjectId.CreateNewId(project.Name);
            if (project.CsprojPath is not null) idsByCsproj[project.CsprojPath] = id;
            _projectDirectories.Add((project.Directory, id));
            infos.Add((project, CreateProjectInfo(id, project)));
        }

        var solution = _workspace.CurrentSolution;

        foreach (var (source, info) in infos)
        {
            var references = source.ProjectReferences
                .Where(idsByCsproj.ContainsKey)
                .Select(path => new ProjectReference(idsByCsproj[path]))
                .ToList();

            solution = solution.AddProject(info.WithProjectReferences(references));
        }

        if (!_workspace.TryApplyChanges(solution))
            throw new InvalidOperationException("Roslyn rejected the initial solution");

        // Longest path first, so a nested project wins over its parent.
        _projectDirectories.Sort((left, right) => right.Directory.Length.CompareTo(left.Directory.Length));

        foreach (var project in _workspace.CurrentSolution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath is not null) _documentsByPath[document.FilePath] = document.Id;
            }
        }

        Log.Info($"loaded {_documentsByPath.Count} document(s) across {discovered.Count} project(s)");
    }

    private static ProjectInfo CreateProjectInfo(ProjectId id, DiscoveredProject project)
    {
        var documents = project.SourceFiles
            .Distinct(PathComparer.Instance)
            .Select(path => DocumentInfo.Create(
                DocumentId.CreateNewId(id),
                Path.GetFileName(path),
                filePath: path,
                // A lazy loader keeps startup fast and memory proportional to
                // what is actually analysed, not to the size of the repository.
                loader: new FileTextLoader(path, System.Text.Encoding.UTF8)))
            .ToList();

        return ProjectInfo.Create(
            id,
            VersionStamp.Create(),
            project.Name,
            project.Name,
            LanguageNames.CSharp,
            filePath: project.CsprojPath,
            compilationOptions: new CSharpCompilationOptions(
                project.OutputKind,
                allowUnsafe: project.AllowUnsafe,
                nullableContextOptions: project.Nullable,
                // Without this, every "using" that resolves through a global
                // using still works, but unresolved ones report awkwardly.
                metadataReferenceResolver: null),
            parseOptions: new CSharpParseOptions(
                project.LanguageVersion,
                preprocessorSymbols: project.PreprocessorSymbols),
            documents: documents,
            metadataReferences: References.ForProject(project.Directory));
    }

    // -----------------------------------------------------------------------
    // Editor synchronisation
    // -----------------------------------------------------------------------

    /// <summary>Applies the editor's buffer contents, adding the file if it is new to us.</summary>
    public Document? Update(string path, string text)
    {
        var sourceText = SourceText.From(text);

        if (_documentsByPath.TryGetValue(path, out var id))
        {
            var updated = _workspace.CurrentSolution.WithDocumentText(id, sourceText, PreservationMode.PreserveIdentity);
            if (!_workspace.TryApplyChanges(updated))
            {
                Log.Warn($"could not apply edit to {path}");
                return null;
            }

            return _workspace.CurrentSolution.GetDocument(id);
        }

        // A file created after startup, or one outside the workspace root that
        // the user simply opened.
        var projectId = ProjectFor(path);
        if (projectId is null)
        {
            Log.Warn($"no project can host {path}");
            return null;
        }

        var documentId = DocumentId.CreateNewId(projectId);
        var info = DocumentInfo.Create(
            documentId,
            Path.GetFileName(path),
            filePath: path,
            loader: TextLoader.From(TextAndVersion.Create(sourceText, VersionStamp.Create(), path)));

        if (!_workspace.TryApplyChanges(_workspace.CurrentSolution.AddDocument(info)))
        {
            Log.Warn($"could not add {path} to the workspace");
            return null;
        }

        _documentsByPath[path] = documentId;
        Log.Debug($"added {path} to project {projectId}");
        return _workspace.CurrentSolution.GetDocument(documentId);
    }

    /// <summary>Drops the editor's version and goes back to what is on disk.</summary>
    public void Close(string path)
    {
        if (!_documentsByPath.TryGetValue(path, out var id)) return;
        if (!File.Exists(path)) return;

        try
        {
            using var stream = File.OpenRead(path);
            var onDisk = SourceText.From(stream, System.Text.Encoding.UTF8);
            _workspace.TryApplyChanges(_workspace.CurrentSolution.WithDocumentText(id, onDisk));
        }
        catch (Exception error)
        {
            Log.Debug($"could not reload {path} from disk: {error.Message}");
        }
    }

    public Document? Find(string path) =>
        _documentsByPath.TryGetValue(path, out var id) ? _workspace.CurrentSolution.GetDocument(id) : null;

    private ProjectId? ProjectFor(string path)
    {
        foreach (var (directory, id) in _projectDirectories)
        {
            var prefix = directory.EndsWith(Path.DirectorySeparatorChar)
                ? directory
                : directory + Path.DirectorySeparatorChar;

            if (path.StartsWith(prefix, PathComparer.Comparison)) return id;
        }

        return _workspace.CurrentSolution.ProjectIds.FirstOrDefault();
    }

    // -----------------------------------------------------------------------
    // Host services
    // -----------------------------------------------------------------------

    /// <remarks>
    /// The default host only composes the Workspaces layer. Completion lives in
    /// the Features layer, so those assemblies have to be added explicitly or
    /// CompletionService.GetService returns null.
    /// </remarks>
    private static MefHostServices CreateHostServices()
    {
        var assemblies = MefHostServices.DefaultAssemblies.ToList();

        foreach (var name in new[] { "Microsoft.CodeAnalysis.Features", "Microsoft.CodeAnalysis.CSharp.Features" })
        {
            try
            {
                assemblies.Add(Assembly.Load(name));
            }
            catch (Exception error)
            {
                Log.Warn($"could not load {name}, completion will be unavailable: {error.Message}");
            }
        }

        return MefHostServices.Create(assemblies.Distinct());
    }

    public void Dispose() => _workspace.Dispose();
}

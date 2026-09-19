using System.Diagnostics;
using System.Text.Json;

namespace CsLite;

/// <summary>
/// The message loop, and the switch that routes each method to a handler.
/// </summary>
/// <remarks>
/// Deliberately single threaded: one message is handled to completion before
/// the next is read. That removes every race between an edit and the analysis
/// of that edit, at the cost of a slow request blocking the ones behind it.
/// For a single developer editing a single buffer that is a good trade, and it
/// keeps the whole server reasoned about as a straight line.
/// </remarks>
internal sealed class Server(MessageStream messages) : IDisposable
{
    private readonly MessageStream _messages = messages;
    private CSharpWorkspace? _workspace;
    private string? _root;
    private bool _shutdownRequested;

    /// <summary>
    /// Whether the editor said it can render markdown in a hover popup. Eglot
    /// advertises plaintext only when markdown-mode is not installed.
    /// </summary>
    private bool _hoverMarkdown = true;

    /// <summary>Files the editor currently has open, so we can refresh their diagnostics.</summary>
    private readonly HashSet<string> _openFiles = new(PathComparer.Instance);

    /// <summary>Polls the editor process, so we do not outlive it.</summary>
    private Timer? _editorWatchdog;

    public int Run()
    {
        while (true)
        {
            JsonDocument? message;
            try
            {
                message = _messages.Read();
            }
            catch (Exception error)
            {
                Log.Error("could not read a message, giving up", error);
                return 1;
            }

            if (message is null)
            {
                // The editor closed the pipe without saying "exit". Eglot does
                // this when Emacs quits, and it is not an error.
                Log.Info("input stream closed, exiting");
                return _shutdownRequested ? 0 : 1;
            }

            using (message)
            {
                var root = message.RootElement;
                var method = root.TryGetProperty("method", out var value) ? value.GetString() : null;
                if (method is null) continue;

                var hasId = root.TryGetProperty("id", out var id);

                if (method == "exit") return _shutdownRequested ? 0 : 1;

                try
                {
                    Handle(method, root, hasId ? id : null);
                }
                catch (Exception error)
                {
                    Log.Error($"{method} failed", error);
                    if (hasId) RespondError(id, error);
                }
            }
        }
    }

    private void Handle(string method, JsonElement message, JsonElement? id)
    {
        var parameters = message.TryGetProperty("params", out var value) ? value : default;

        switch (method)
        {
            case "initialize":
                Respond(id!.Value, Initialize(parameters));
                break;

            case "initialized":
                // Loading happens here rather than during "initialize" so the
                // editor gets its handshake back immediately, however long the
                // workspace takes to scan.
                LoadWorkspace();
                break;

            case "shutdown":
                _shutdownRequested = true;
                Respond(id!.Value, null);
                break;

            case "textDocument/didOpen":
            {
                var request = Parse<DidOpenParams>(parameters);
                var path = Uris.ToPath(request.TextDocument.Uri);
                _openFiles.Add(path);
                Analyse(path, request.TextDocument.Text);
                break;
            }

            case "textDocument/didChange":
            {
                var request = Parse<DidChangeParams>(parameters);
                var path = Uris.ToPath(request.TextDocument.Uri);
                var last = request.ContentChanges.LastOrDefault();
                if (last is not null) Analyse(path, last.Text);
                break;
            }

            case "textDocument/didSave":
            {
                var request = Parse<TextDocumentParams>(parameters);
                var path = Uris.ToPath(request.TextDocument.Uri);
                var document = _workspace?.Find(path);
                if (document is not null) Publish(path, RunSync(Features.DiagnoseAsync(document, default)));
                break;
            }

            case "textDocument/didClose":
            {
                var request = Parse<TextDocumentParams>(parameters);
                var path = Uris.ToPath(request.TextDocument.Uri);
                _openFiles.Remove(path);
                _workspace?.Close(path);
                // Clear the buffer's diagnostics; nothing is displaying them now.
                Publish(path, []);
                break;
            }

            case "textDocument/hover":
                Respond(id!.Value, WithDocument(parameters, (document, position) =>
                    RunSync(Features.HoverAsync(document, position, _hoverMarkdown, default))));
                break;

            case "textDocument/definition":
                Respond(id!.Value, WithDocument(parameters, (document, position) =>
                    RunSync(Features.DefinitionAsync(document, position, default))));
                break;

            case "textDocument/completion":
                Respond(id!.Value, WithDocument(parameters, (document, position) =>
                    RunSync(Features.CompleteAsync(document, position, default))));
                break;

            case "textDocument/signatureHelp":
                Respond(id!.Value, WithDocument(parameters, (document, position) =>
                    RunSync(SignatureHelpProvider.ForAsync(document, position, default))));
                break;

            case "textDocument/documentSymbol":
            {
                var request = Parse<TextDocumentParams>(parameters);
                var document = DocumentFor(request.TextDocument.Uri);
                Respond(id!.Value, document is null
                    ? null
                    : RunSync(DocumentSymbols.ForAsync(document, default)));
                break;
            }

            case "textDocument/codeAction":
            {
                var request = Parse<CodeActionParams>(parameters);
                var document = DocumentFor(request.TextDocument.Uri);
                Respond(id!.Value, document is null
                    ? null
                    : RunSync(CodeActions.ForAsync(document, request.Range, default)));
                break;
            }

            case "textDocument/references":
            {
                var request = Parse<ReferenceParams>(parameters);
                var document = DocumentFor(request.TextDocument.Uri);
                Respond(id!.Value, document is null
                    ? null
                    : RunSync(Features.ReferencesAsync(
                        document,
                        request.Position,
                        request.Context?.IncludeDeclaration ?? true,
                        default)));
                break;
            }

            case "textDocument/rename":
            {
                var request = Parse<RenameParams>(parameters);
                var document = DocumentFor(request.TextDocument.Uri);
                Respond(id!.Value, document is null
                    ? null
                    : RunSync(Features.RenameAsync(document, request.Position, request.NewName, default)));
                break;
            }

            case "$/cancelRequest":
            case "$/setTrace":
            case "workspace/didChangeConfiguration":
                break;  // nothing to do, and answering would be wrong

            default:
                Log.Debug($"unhandled method {method}");
                if (id is not null)
                {
                    _messages.Write(new ErrorResponseMessage
                    {
                        Id = id.Value,
                        Error = new ResponseError { Code = ErrorCodes.MethodNotFound, Message = method },
                    });
                }
                break;
        }
    }

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    private InitializeResult Initialize(JsonElement parameters)
    {
        _root = RootFrom(parameters) ?? Directory.GetCurrentDirectory();
        _hoverMarkdown = AcceptsMarkdownHover(parameters);
        WatchEditor(parameters);

        Log.Info($"workspace root: {_root}");
        Log.Info($"hover format: {(_hoverMarkdown ? "markdown" : "plaintext")}");

        return new InitializeResult(
            new ServerCapabilities
            {
                TextDocumentSync = 1,
                HoverProvider = true,
                DefinitionProvider = true,
                ReferencesProvider = true,
                RenameProvider = true,
                SignatureHelpProvider = new SignatureHelpOptions(),
                CodeActionProvider = true,
                DocumentSymbolProvider = true,
                CompletionProvider = new CompletionOptions { TriggerCharacters = ["."] },
            },
            new ServerInfo("cslite", "0.1.0"));
    }

    /// <summary>
    /// Exits if the editor that started us disappears.
    /// </summary>
    /// <remarks>
    /// Normally the pipe closing is enough: stdin reaches end of stream and the
    /// loop returns. But an editor that is killed outright, or that leaves the
    /// handle open in a child, can strand this process running forever. A
    /// stranded server holds its binary and its log file open, which on Windows
    /// blocks the next build and stops the replacement server from starting.
    /// The editor tells us its process id at initialize for exactly this, so
    /// poll it and leave when it is gone.
    /// </remarks>
    private void WatchEditor(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("processId", out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var processId))
        {
            Log.Debug("the editor did not name a process to watch");
            return;
        }

        Log.Info($"watching editor process {processId}");

        var interval = TimeSpan.FromSeconds(10);
        _editorWatchdog = new Timer(
            _ =>
            {
                if (IsRunning(processId)) return;

                Log.Info($"editor process {processId} is gone, exiting");
                Log.Close();
                Environment.Exit(0);
            },
            null, interval, interval);
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;   // no process with that id
        }
        catch (Exception error)
        {
            // Never exit on the strength of a question we could not ask.
            Log.Debug($"could not check process {processId}: {error.Message}");
            return true;
        }
    }

    /// <summary>
    /// Reads textDocument.hover.contentFormat from the client capabilities.
    /// </summary>
    /// <remarks>
    /// When the key is missing entirely we assume markdown, which is what
    /// nearly every editor handles; when it is present we take it literally.
    /// </remarks>
    private static bool AcceptsMarkdownHover(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return true;

        if (!parameters.TryGetProperty("capabilities", out var capabilities)
            || !capabilities.TryGetProperty("textDocument", out var textDocument)
            || !textDocument.TryGetProperty("hover", out var hover)
            || !hover.TryGetProperty("contentFormat", out var formats)
            || formats.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        foreach (var format in formats.EnumerateArray())
        {
            if (format.ValueKind == JsonValueKind.String
                && string.Equals(format.GetString(), "markdown", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Prefers workspaceFolders, falling back through the deprecated fields.</summary>
    private static string? RootFrom(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;

        if (parameters.TryGetProperty("workspaceFolders", out var folders)
            && folders.ValueKind == JsonValueKind.Array
            && folders.GetArrayLength() > 0
            && folders[0].TryGetProperty("uri", out var folderUri)
            && folderUri.GetString() is { } uri)
        {
            return Uris.ToPath(uri);
        }

        if (parameters.TryGetProperty("rootUri", out var rootUri)
            && rootUri.ValueKind == JsonValueKind.String
            && rootUri.GetString() is { } value)
        {
            return Uris.ToPath(value);
        }

        if (parameters.TryGetProperty("rootPath", out var rootPath)
            && rootPath.ValueKind == JsonValueKind.String)
        {
            return rootPath.GetString();
        }

        return null;
    }

    private void LoadWorkspace()
    {
        if (_root is null) return;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            _workspace = new CSharpWorkspace();
            _workspace.Load(_root);
            Log.Info($"workspace ready in {stopwatch.ElapsedMilliseconds} ms");
        }
        catch (Exception error)
        {
            Log.Error("failed to load the workspace", error);
            _workspace = null;
        }
    }

    // -----------------------------------------------------------------------
    // Analysis
    // -----------------------------------------------------------------------

    private void Analyse(string path, string text)
    {
        var document = _workspace?.Update(path, text);
        if (document is null) return;

        var stopwatch = Stopwatch.StartNew();
        var diagnostics = RunSync(Features.DiagnoseAsync(document, default));
        Log.Debug($"{Path.GetFileName(path)}: {diagnostics.Count} diagnostic(s) in {stopwatch.ElapsedMilliseconds} ms");

        Publish(path, diagnostics);
    }

    private void Publish(string path, IReadOnlyList<Diagnostic> diagnostics)
    {
        _messages.Write(new NotificationMessage
        {
            Method = "textDocument/publishDiagnostics",
            Params = new PublishDiagnosticsParams(Uris.FromPath(path), diagnostics),
        });
    }

    // -----------------------------------------------------------------------
    // Plumbing
    // -----------------------------------------------------------------------

    /// <summary>Resolves the document a positional request refers to, or returns null.</summary>
    private T? WithDocument<T>(JsonElement parameters, Func<Microsoft.CodeAnalysis.Document, Position, T?> handler)
        where T : class
    {
        var request = Parse<TextDocumentPositionParams>(parameters);
        var document = DocumentFor(request.TextDocument.Uri);

        return document is null ? null : handler(document, request.Position);
    }

    private Microsoft.CodeAnalysis.Document? DocumentFor(string uri)
    {
        var path = Uris.ToPath(uri);
        var document = _workspace?.Find(path);

        if (document is null) Log.Debug($"no document for {path}");
        return document;
    }

    private static T Parse<T>(JsonElement parameters) =>
        parameters.Deserialize<T>(Json.Options)
        ?? throw new InvalidDataException($"could not read {typeof(T).Name} from the request");

    /// <summary>
    /// Bridges Roslyn's async API into our synchronous loop. Safe here only
    /// because there is no synchronisation context to deadlock against.
    /// </summary>
    private static T RunSync<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private void Respond(JsonElement id, object? result) =>
        _messages.Write(new ResponseMessage { Id = id, Result = result });

    private void RespondError(JsonElement id, Exception error) =>
        _messages.Write(new ErrorResponseMessage
        {
            Id = id,
            Error = new ResponseError { Code = ErrorCodes.InternalError, Message = error.Message },
        });

    public void Dispose()
    {
        _editorWatchdog?.Dispose();
        _workspace?.Dispose();
    }
}

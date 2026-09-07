using System.Text.Json;
using System.Text.Json.Serialization;

namespace CsLite;

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

// ---------------------------------------------------------------------------
// JSON-RPC envelopes
// ---------------------------------------------------------------------------

internal sealed class ResponseMessage
{
    public string Jsonrpc => "2.0";

    public JsonElement Id { get; init; }

    /// <remarks>
    /// A successful response must carry a "result" key even when the value is
    /// null (shutdown, for instance), so this one property opts out of the
    /// global null-stripping.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public object? Result { get; init; }
}

/// <summary>
/// A failed response. Separate from <see cref="ResponseMessage"/> because
/// JSON-RPC allows exactly one of "result" and "error", and the success case
/// has to emit "result" even when it is null.
/// </summary>
internal sealed class ErrorResponseMessage
{
    public string Jsonrpc => "2.0";

    public JsonElement Id { get; init; }

    public required ResponseError Error { get; init; }
}

internal sealed class ResponseError
{
    public required int Code { get; init; }
    public required string Message { get; init; }
}

internal sealed class NotificationMessage
{
    public string Jsonrpc => "2.0";
    public required string Method { get; init; }
    public object? Params { get; init; }
}

internal static class ErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}

// ---------------------------------------------------------------------------
// Shared structures
// ---------------------------------------------------------------------------

internal sealed record Position(int Line, int Character);

internal sealed record Range(Position Start, Position End);

internal sealed record Location(string Uri, Range Range);

internal sealed record MarkupContent(string Kind, string Value)
{
    public static MarkupContent Markdown(string value) => new("markdown", value);

    public static MarkupContent PlainText(string value) => new("plaintext", value);
}

// ---------------------------------------------------------------------------
// Lifecycle
// ---------------------------------------------------------------------------

internal sealed record ServerInfo(string Name, string Version);

internal sealed record InitializeResult(ServerCapabilities Capabilities, ServerInfo ServerInfo);

internal sealed class ServerCapabilities
{
    /// <summary>1 = full document sync. We never ask for incremental changes.</summary>
    public int TextDocumentSync { get; init; } = 1;

    public bool HoverProvider { get; init; }
    public bool DefinitionProvider { get; init; }
    public bool ReferencesProvider { get; init; }
    public bool RenameProvider { get; init; }
    public SignatureHelpOptions? SignatureHelpProvider { get; init; }
    public CompletionOptions? CompletionProvider { get; init; }
}

internal sealed class CompletionOptions
{
    public string[] TriggerCharacters { get; init; } = ["."];
    public bool ResolveProvider { get; init; }
}

// ---------------------------------------------------------------------------
// Diagnostics
// ---------------------------------------------------------------------------

internal sealed record Diagnostic(
    Range Range,
    int Severity,
    string Code,
    string Source,
    string Message);

internal sealed record PublishDiagnosticsParams(string Uri, IReadOnlyList<Diagnostic> Diagnostics);

internal static class DiagnosticSeverity
{
    public const int Error = 1;
    public const int Warning = 2;
    public const int Information = 3;
    public const int Hint = 4;
}

// ---------------------------------------------------------------------------
// Language features
// ---------------------------------------------------------------------------

internal sealed record Hover(MarkupContent Contents, Range? Range);

internal sealed record CompletionItem(string Label)
{
    public int? Kind { get; init; }
    public string? Detail { get; init; }
    public string? SortText { get; init; }
    public string? FilterText { get; init; }
    public string? InsertText { get; init; }
}

internal sealed record CompletionList(bool IsIncomplete, IReadOnlyList<CompletionItem> Items);

/// <summary>The subset of LSP completion kinds we map Roslyn's tags onto.</summary>
internal static class CompletionItemKind
{
    public const int Text = 1;
    public const int Method = 2;
    public const int Function = 3;
    public const int Constructor = 4;
    public const int Field = 5;
    public const int Variable = 6;
    public const int Class = 7;
    public const int Interface = 8;
    public const int Module = 9;
    public const int Property = 10;
    public const int Unit = 11;
    public const int Value = 12;
    public const int Enum = 13;
    public const int Keyword = 14;
    public const int Snippet = 15;
    public const int Color = 16;
    public const int File = 17;
    public const int Reference = 18;
    public const int Folder = 19;
    public const int EnumMember = 20;
    public const int Constant = 21;
    public const int Struct = 22;
    public const int Event = 23;
    public const int Operator = 24;
    public const int TypeParameter = 25;
}

// ---------------------------------------------------------------------------
// Request payloads
// ---------------------------------------------------------------------------

internal sealed record TextDocumentIdentifier(string Uri);

internal sealed record TextDocumentItem(string Uri, string LanguageId, int Version, string Text);

internal sealed record DidOpenParams(TextDocumentItem TextDocument);

/// <summary>We advertise full sync, so a change always carries the whole buffer.</summary>
internal sealed record ContentChange(string Text);

internal sealed record DidChangeParams(TextDocumentIdentifier TextDocument, ContentChange[] ContentChanges);

internal sealed record TextDocumentParams(TextDocumentIdentifier TextDocument);

internal sealed record TextDocumentPositionParams(TextDocumentIdentifier TextDocument, Position Position);

// ---------------------------------------------------------------------------
// References and rename
// ---------------------------------------------------------------------------

internal sealed record ReferenceContext(bool IncludeDeclaration);

internal sealed record ReferenceParams(
    TextDocumentIdentifier TextDocument,
    Position Position,
    ReferenceContext? Context);

internal sealed record RenameParams(
    TextDocumentIdentifier TextDocument,
    Position Position,
    string NewName);

internal sealed record TextEdit(Range Range, string NewText);

/// <summary>
/// Edits grouped by document URI. The keys are URIs rather than names, so they
/// deliberately escape the camel-casing applied to property names.
/// </summary>
internal sealed record WorkspaceEdit(IReadOnlyDictionary<string, IReadOnlyList<TextEdit>> Changes);

// ---------------------------------------------------------------------------
// Signature help
// ---------------------------------------------------------------------------

internal sealed class SignatureHelpOptions
{
    public string[] TriggerCharacters { get; init; } = ["(", ","];
    public string[] RetriggerCharacters { get; init; } = [","];
}

/// <summary>
/// A parameter's position within its signature label, as [start, end) offsets.
/// Offsets are used rather than the parameter text so the client never has to
/// locate it by substring search.
/// </summary>
internal sealed record ParameterInformation(int[] Label);

internal sealed record SignatureInformation(
    string Label,
    string? Documentation,
    IReadOnlyList<ParameterInformation> Parameters);

internal sealed record SignatureHelp(
    IReadOnlyList<SignatureInformation> Signatures,
    int ActiveSignature,
    int ActiveParameter);

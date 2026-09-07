using System.Text;
using System.Text.Json;

namespace CsLite;

/// <summary>
/// The LSP base protocol: an ASCII header block, a blank line, then exactly
/// Content-Length <em>bytes</em> of UTF-8 JSON.
/// </summary>
/// <remarks>
/// Two details here cause most "my language server mysteriously hangs" bugs, so
/// they are worth stating plainly. Content-Length counts bytes, not characters,
/// so the body is always measured after UTF-8 encoding. And the header
/// terminator is CRLF on every platform, including Linux.
/// </remarks>
internal sealed class MessageStream(Stream input, Stream output)
{
    private readonly Stream _input = input;
    private readonly Stream _output = output;
    private readonly object _writeGate = new();

    /// <summary>Reads one message, or returns null once the editor closes the pipe.</summary>
    public JsonDocument? Read()
    {
        var contentLength = -1;

        while (true)
        {
            var line = ReadHeaderLine();
            if (line is null) return null;      // clean end of stream
            if (line.Length == 0) break;        // blank line closes the header block

            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                // Tolerate a malformed header rather than tearing down the session.
                Log.Warn($"ignoring malformed header line: {line}");
                continue;
            }

            var name = line.AsSpan(0, colon).Trim();
            var value = line.AsSpan(colon + 1).Trim();

            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, out var parsed))
            {
                contentLength = parsed;
            }
        }

        if (contentLength < 0)
            throw new InvalidDataException("message header had no usable Content-Length");

        var body = new byte[contentLength];
        _input.ReadExactly(body);
        return JsonDocument.Parse(body);
    }

    public void Write(object message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Json.Options);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        // Responses and server-initiated notifications can be produced from
        // different places; keep a whole message contiguous on the wire.
        lock (_writeGate)
        {
            _output.Write(header);
            _output.Write(body);
            _output.Flush();
        }
    }

    /// <summary>Reads one CRLF-terminated ASCII header line, without its terminator.</summary>
    private string? ReadHeaderLine()
    {
        var builder = new StringBuilder(64);

        while (true)
        {
            var b = _input.ReadByte();
            if (b == -1) return builder.Length == 0 ? null : builder.ToString();
            if (b == '\n') return builder.ToString();
            if (b != '\r') builder.Append((char)b);
        }
    }
}

using Microsoft.CodeAnalysis.Text;

namespace CsLite;

internal static class Uris
{
    /// <summary>Converts a document URI from the editor into a local path.</summary>
    /// <remarks>
    /// Emacs percent-encodes the colon of a Windows drive letter, so a buffer
    /// arrives as "file:///c%3A/Users/x". <see cref="Uri"/> does not recognise
    /// that as a DOS path, and <see cref="Uri.LocalPath"/> hands back
    /// "/c:/Users/x" with the leading slash still attached. Left alone,
    /// <see cref="Path.GetFullPath(string)"/> then resolves it against the
    /// current drive and yields "c:\c:\Users\x", which matches no document at
    /// all. Trimming that slash is what makes the server work with Eglot on
    /// Windows.
    /// </remarks>
    public static string ToPath(string uri)
    {
        var parsed = new Uri(uri);
        var path = parsed.LocalPath;

        if (path.Length >= 3
            && path[0] is '/' or '\\'
            && char.IsLetter(path[1])
            && path[2] == ':')
        {
            path = path[1..];
        }

        return Path.GetFullPath(path);
    }

    public static string FromPath(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;
}

internal static class Conversions
{
    /// <summary>
    /// Turns an LSP position into an offset into the buffer.
    /// </summary>
    /// <remarks>
    /// LSP counts characters in UTF-16 code units, which is exactly what
    /// <see cref="SourceText"/> indexes by, so no re-encoding is needed. It also
    /// means an emoji counts as two, which is why this must never be written as
    /// a byte offset. Out-of-range values are clamped rather than rejected:
    /// editors routinely send a position one keystroke ahead of the text.
    /// </remarks>
    public static int ToOffset(SourceText text, Position position)
    {
        if (position.Line < 0) return 0;
        if (position.Line >= text.Lines.Count) return text.Length;

        var line = text.Lines[position.Line];
        return Math.Clamp(line.Start + position.Character, line.Start, line.End);
    }

    public static Position ToPosition(SourceText text, int offset)
    {
        var linePosition = text.Lines.GetLinePosition(Math.Clamp(offset, 0, text.Length));
        return new Position(linePosition.Line, linePosition.Character);
    }

    public static Range ToRange(SourceText text, TextSpan span)
    {
        var lineSpan = text.Lines.GetLinePositionSpan(span);
        return new Range(
            new Position(lineSpan.Start.Line, lineSpan.Start.Character),
            new Position(lineSpan.End.Line, lineSpan.End.Character));
    }
}

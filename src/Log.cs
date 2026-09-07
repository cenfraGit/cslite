namespace CsLite;

/// <summary>
/// Diagnostics about the server itself.
/// </summary>
/// <remarks>
/// This never touches stdout, because stdout is the protocol transport. Eglot
/// collects our stderr into the "*EGLOT (project/csharp-mode) stderr*" buffer,
/// which makes stderr the natural default sink.
/// </remarks>
internal static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;

    public static bool Verbose { get; set; }

    /// <summary>Adds a file sink, if one can be opened.</summary>
    /// <remarks>
    /// Two servers run at once as soon as two C# projects are open, and both
    /// are configured with the same log path. The file is therefore opened
    /// shared, and a failure to open it is never fatal: losing the log is a
    /// nuisance, but failing to start is the editor reporting "server died".
    /// </remarks>
    public static void OpenFile(string path)
    {
        lock (Gate)
        {
            _file?.Dispose();
            _file = null;

            try
            {
                var stream = new FileStream(
                    path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

                _file = new StreamWriter(stream) { AutoFlush = true };
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"could not open log file {path}, continuing on stderr: {error.Message}");
            }
        }
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Debug(string message)
    {
        if (Verbose) Write("DEBUG", message);
    }

    public static void Error(string message, Exception? error = null)
    {
        Write("ERROR", error is null ? message : $"{message}: {error}");
    }

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {level} {message}";
        lock (Gate)
        {
            Console.Error.WriteLine(line);
            _file?.WriteLine(line);
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }
}

namespace CsLite;

internal static class Program
{
    private const string Usage = """
        cslite - a small C# language server

        usage: cslite [options]

        The server speaks LSP over stdin/stdout and is meant to be started by
        your editor, not by hand.

          --log <path>   also write the server log to a file
          --verbose      include debug detail in the log
          --version      print the version and exit
          --help         print this message and exit
        """;

    private static int Main(string[] args)
    {
        foreach (var argument in args)
        {
            switch (argument)
            {
                case "--help" or "-h":
                    Console.Error.WriteLine(Usage);
                    return 0;

                case "--version":
                    Console.Error.WriteLine("cslite 0.1.0");
                    return 0;

                case "--verbose" or "-v":
                    Log.Verbose = true;
                    break;
            }
        }

        var logIndex = Array.IndexOf(args, "--log");
        if (logIndex >= 0 && logIndex + 1 < args.Length) Log.OpenFile(args[logIndex + 1]);

        // Stdout belongs to the protocol. Any stray Console.WriteLine, from our
        // code or from a library, would splice raw text into the JSON stream
        // and wedge the session, so we take the real handle for ourselves and
        // point Console.Out at stderr before anything else can write.
        var transport = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);

        var input = new BufferedStream(Console.OpenStandardInput());

        // A crash on a thread pool thread kills the process without passing
        // through the handler loop, and the editor then reports only "server
        // died". Recording it here is the difference between a mystery and a
        // stack trace.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("unhandled exception, the process is going down", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("unobserved task exception", e.Exception);
            e.SetObserved();
        };

        Log.Info($"cslite starting on {Environment.OSVersion.VersionString}, .NET {Environment.Version}");

        try
        {
            using var server = new Server(new MessageStream(input, transport));
            return server.Run();
        }
        catch (Exception error)
        {
            Log.Error("the server stopped unexpectedly", error);
            return 1;
        }
        finally
        {
            Log.Close();
        }
    }
}

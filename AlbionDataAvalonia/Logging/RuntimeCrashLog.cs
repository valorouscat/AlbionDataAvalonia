using Serilog;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace AlbionDataAvalonia.Logging;

/// <summary>Best-effort evidence for fatal managed errors, including threads that never reach Program.Main's catch.</summary>
public static class RuntimeCrashLog
{
    private static int installed;
    private static readonly object fileLock = new();
    private static string mapState = "Map has not been opened.";
    public static string MapState { get => Volatile.Read(ref mapState); set => Volatile.Write(ref mapState, value); }

    public static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) != 0) return;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Record("Unhandled managed thread", args.ExceptionObject as Exception
            ?? new Exception(args.ExceptionObject?.ToString() ?? "Unknown exception"), terminating: args.IsTerminating);
    }

    public static void Record(string source, Exception exception, bool terminating = false)
    {
        // Do not try to query the tracker or dispatcher here: the failing thread may already hold their locks.
        var context = MapState;
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AFMDataClient", "logs");
            lock (fileLock)
            {
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, "runtime-crash.txt");
                if (File.Exists(file) && new FileInfo(file).Length > 1_048_576)
                    File.Move(file, file + ".previous", overwrite: true);
                File.AppendAllText(file, $"{DateTime.UtcNow:O} | {source} | PID {Environment.ProcessId} | thread {Environment.CurrentManagedThreadId}\n"
                    + $"Client {typeof(App).Assembly.GetName().Version} | .NET {Environment.Version} | {RuntimeInformation.OSDescription}\nLast map render: {context}\n{exception}\n\n");
            }
        }
        catch { /* A full or inaccessible disk must not replace the original error. */ }
        try { Log.Fatal(exception, "Fatal managed error in {Source}. {MapState}", source, context); if (terminating) Log.CloseAndFlush(); }
        catch { }
    }
}

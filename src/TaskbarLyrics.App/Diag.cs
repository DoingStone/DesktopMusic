using System.IO;

namespace TaskbarLyrics.App;

/// <summary>
/// Opt-in file logging, enabled by setting <c>TBL_DIAG=1</c>.
/// <para>
/// The app is a windowless WinExe, so console output is unavailable; writing to
/// a file is the only practical way to debug rendering on a user's machine.
/// </para>
/// </summary>
public static class Diag
{
    /// <summary>
    /// True when <c>TBL_DIAG=1</c>. Exposed so that hot paths can skip the call entirely:
    /// <c>Diag.Log($"{...}")</c> evaluates its argument at the call site, so the guard
    /// inside <see cref="Log"/> does not save the formatting cost.
    /// </summary>
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("TBL_DIAG") == "1";

    private static readonly string LogPath = Path.Combine(
        Path.GetTempPath(), "taskbar-lyrics-diag.log");

    private static readonly object Gate = new();

    public static void Log(string message)
    {
        if (!Enabled) return;

        try
        {
            lock (Gate)
            {
                // Flush immediately: the last lines before a forced exit are the
                // most interesting ones and must survive process teardown.
                using var stream = new FileStream(
                    LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);

                writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
        }
        catch
        {
            // Diagnostics must never break the app.
        }
    }

    public static void Reset()
    {
        if (!Enabled) return;
        try { File.WriteAllText(LogPath, string.Empty); } catch { }
    }
}

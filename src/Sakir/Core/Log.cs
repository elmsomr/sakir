using System.Text;

namespace Sakir.Core;

/// <summary>
/// Minimal file log next to the executable. A tray app that runs unattended
/// needs one: when the render loop dies there is no console to look at, and the
/// UI can only ever show the last error.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string Path0 =
        Path.Combine(AppContext.BaseDirectory, "Sakir.log");

    private static readonly Dictionary<string, DateTime> LastSeen = new();

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null)
    {
        string text = ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}";
        if (ex?.StackTrace is { } st) text += Environment.NewLine + st;
        Write("ERROR", text);
    }

    /// <summary>
    /// Logs at most once per interval for a given key. The render loop can fail
    /// ten times a second; without this the log would be useless.
    /// </summary>
    public static void Throttled(string key, string message, Exception? ex = null, int seconds = 30)
    {
        lock (Gate)
        {
            if (LastSeen.TryGetValue(key, out DateTime t) &&
                (DateTime.UtcNow - t).TotalSeconds < seconds) return;
            LastSeen[key] = DateTime.UtcNow;
        }
        Error(message, ex);
    }

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                // keep it from growing without bound
                var fi = new FileInfo(Path0);
                if (fi.Exists && fi.Length > 512 * 1024)
                    File.Move(Path0, Path0 + ".1", overwrite: true);

                File.AppendAllText(Path0, line, Encoding.UTF8);
            }
            catch { /* logging must never take the app down */ }
        }
    }
}

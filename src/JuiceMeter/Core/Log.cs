using System.Globalization;

namespace JuiceMeter.Core;

internal static class Log
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERR ", ex is null ? message : message + " :: " + ex);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var path = Paths.LogFile;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var old = path + ".1";
                    File.Delete(old);
                    File.Move(path, old);
                }

                var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                File.AppendAllText(path, $"{stamp} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}

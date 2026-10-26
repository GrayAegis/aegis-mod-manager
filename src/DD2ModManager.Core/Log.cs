namespace DD2ModManager.Core;

public enum LogLevel { Info, Warn, Error }

/// <summary>Minimal app-wide log: appends to a file and raises an event the UI can show.</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static string? FilePath { get; set; }
    public static event Action<LogLevel, string>? Written;

    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Write(LogLevel level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        lock (Gate)
        {
            if (FilePath != null)
            {
                try { File.AppendAllText(FilePath, line + Environment.NewLine); }
                catch (IOException) { }
            }
        }
        Written?.Invoke(level, message);
    }
}

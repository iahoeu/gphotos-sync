using System.Text;

namespace GPhotosSync.Data;

public static class Logger
{
    private static readonly object LockObj = new();
    public static event Action<string>? OnLogAdded;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
        try
        {
            lock (LockObj)
            {
                Directory.CreateDirectory(AppConfig.AppDataFolder);
                File.AppendAllText(AppConfig.LogFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Ignore file logging errors
        }

        OnLogAdded?.Invoke(line);
    }

    public static string ReadRecentLogs(int maxLines = 200)
    {
        try
        {
            lock (LockObj)
            {
                if (!File.Exists(AppConfig.LogFilePath))
                    return "Zatím žádné záznamy v protokolu.";

                var lines = File.ReadAllLines(AppConfig.LogFilePath, Encoding.UTF8);
                return string.Join(Environment.NewLine, lines.TakeLast(maxLines));
            }
        }
        catch (Exception ex)
        {
            return $"Nelze načíst protokol: {ex.Message}";
        }
    }
}

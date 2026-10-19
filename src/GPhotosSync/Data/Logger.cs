using System.Text;

namespace GPhotosSync.Data;

public static class Logger
{
    private static readonly object LockObj = new();
    public static event Action<string>? OnLogAdded;

    private const long MaxLogFileBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Podrobný protokol (jednotlivé kroky skenování, navigace). Ve výchozím stavu vypnutý.
    /// </summary>
    public static bool VerboseEnabled { get; set; }

    public static string OldLogFilePath => Path.Combine(AppConfig.AppDataFolder, "sync.old.log");

    public static void Debug(string message)
    {
        if (VerboseEnabled)
            Write("DEBUG", message);
    }

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
                RotateIfTooLarge();
                File.AppendAllText(AppConfig.LogFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Ignore file logging errors
        }

        OnLogAdded?.Invoke(line);
    }

    /// <summary>
    /// Při překročení 5 MB se protokol přesune do sync.old.log (ponechá se jen jeden starší soubor).
    /// </summary>
    private static void RotateIfTooLarge()
    {
        var info = new FileInfo(AppConfig.LogFilePath);
        if (!info.Exists || info.Length < MaxLogFileBytes)
            return;

        File.Move(AppConfig.LogFilePath, OldLogFilePath, overwrite: true);
    }

    public static string ReadRecentLogs(int maxLines = 200)
    {
        try
        {
            lock (LockObj)
            {
                if (!File.Exists(AppConfig.LogFilePath))
                    return "Zatím žádné záznamy v protokolu.";

                // Číst jen konec souboru, ne celý protokol
                const int tailBytes = 128 * 1024;
                using var fs = new FileStream(AppConfig.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length > tailBytes)
                    fs.Seek(-tailBytes, SeekOrigin.End);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                var lines = reader.ReadToEnd().Split('\n').Select(x => x.TrimEnd('\r')).Where(x => x.Length > 0).ToList();
                if (fs.Length > tailBytes && lines.Count > 0)
                    lines.RemoveAt(0); // první řádek může být useknutý
                return string.Join(Environment.NewLine, lines.TakeLast(maxLines));
            }
        }
        catch (Exception ex)
        {
            return $"Nelze načíst protokol: {ex.Message}";
        }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace GPhotosSync.Data;

public enum InitialSyncMode
{
    OnlyNewFromNow = 0,
    LastDays = 1,
    FromDate = 2,
    AllHistory = 3
}

public class AppConfig
{
    public string DownloadFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "Google Photos");

    public int SyncIntervalMinutes { get; set; } = 5;
    public bool DownloadVideos { get; set; } = true;
    public bool TwoWayDelete { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;

    public InitialSyncMode InitialMode { get; set; } = InitialSyncMode.OnlyNewFromNow;
    public int InitialSyncDays { get; set; } = 7;
    public DateTime InitialSyncFromDate { get; set; } = DateTime.Today.AddDays(-7);

    public bool IsFirstRunCompleted { get; set; } = false;
    public bool BaselineCaptured { get; set; } = false;
    public DateTime InstalledAt { get; set; } = DateTime.Now;

    // Gitea Auto-Update Configuration
    public bool AutoCheckUpdates { get; set; } = true;
    public bool AutoInstallUpdates { get; set; } = false;
    public string GiteaServerUrl { get; set; } = "https://git.iaho.eu";
    public string GiteaRepoOwner { get; set; } = "iaho";
    public string GiteaRepoName { get; set; } = "gphotos-sync";
    public string GiteaAccessToken { get; set; } = ""; // volitelné pro privátní repozitář

    public static string AppDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GPhotosSync");

    public static string ConfigFilePath => Path.Combine(AppDataFolder, "config.json");
    public static string WebViewProfileFolder => Path.Combine(AppDataFolder, "WebViewProfile");
    public static string DatabaseFilePath => Path.Combine(AppDataFolder, "sync_history.db");
    public static string LogFilePath => Path.Combine(AppDataFolder, "sync.log");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppConfig Load()
    {
        try
        {
            Directory.CreateDirectory(AppDataFolder);
            if (File.Exists(ConfigFilePath))
            {
                var json = File.ReadAllText(ConfigFilePath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (cfg != null)
                {
                    return cfg;
                }
            }

            // Check if the Windows Installer created installer_defaults.json next to the executable or in AppData
            var defaultCfg = new AppConfig();
            ApplyInstallerDefaults(defaultCfg);
            defaultCfg.Save();
            return defaultCfg;
        }
        catch (Exception ex)
        {
            Logger.Error($"Chyba při načítání konfigurace: {ex.Message}");
            return new AppConfig();
        }
    }

    private static void ApplyInstallerDefaults(AppConfig cfg)
    {
        try
        {
            string[] candidatePaths =
            [
                Path.Combine(AppDataFolder, "installer_defaults.json"),
                Path.Combine(AppContext.BaseDirectory, "installer_defaults.json")
            ];

            foreach (var path in candidatePaths)
            {
                if (!File.Exists(path)) continue;

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;

                if (root.TryGetProperty("DownloadFolder", out var folderProp) &&
                    !string.IsNullOrWhiteSpace(folderProp.GetString()))
                {
                    cfg.DownloadFolder = folderProp.GetString()!;
                }

                if (root.TryGetProperty("TwoWayDelete", out var twoWayProp))
                {
                    cfg.TwoWayDelete = twoWayProp.ValueKind == JsonValueKind.True ||
                                       (twoWayProp.ValueKind == JsonValueKind.String && twoWayProp.GetString() == "1");
                }

                if (root.TryGetProperty("DownloadVideos", out var videoProp))
                {
                    cfg.DownloadVideos = videoProp.ValueKind == JsonValueKind.True ||
                                         (videoProp.ValueKind == JsonValueKind.String && videoProp.GetString() == "1");
                }

                if (root.TryGetProperty("InitialMode", out var modeProp))
                {
                    var modeStr = modeProp.GetString();
                    cfg.InitialMode = modeStr switch
                    {
                        "only_new" => InitialSyncMode.OnlyNewFromNow,
                        "last_7_days" => InitialSyncMode.LastDays,
                        "last_30_days" => InitialSyncMode.LastDays,
                        "all" => InitialSyncMode.AllHistory,
                        _ => InitialSyncMode.OnlyNewFromNow
                    };
                    if (modeStr == "last_30_days") cfg.InitialSyncDays = 30;
                    if (modeStr == "last_7_days") cfg.InitialSyncDays = 7;
                }

                if (root.TryGetProperty("GiteaServerUrl", out var giteaUrl) && !string.IsNullOrWhiteSpace(giteaUrl.GetString()))
                    cfg.GiteaServerUrl = giteaUrl.GetString()!;
                if (root.TryGetProperty("GiteaRepoOwner", out var giteaOwner) && !string.IsNullOrWhiteSpace(giteaOwner.GetString()))
                    cfg.GiteaRepoOwner = giteaOwner.GetString()!;
                if (root.TryGetProperty("GiteaRepoName", out var giteaRepo) && !string.IsNullOrWhiteSpace(giteaRepo.GetString()))
                    cfg.GiteaRepoName = giteaRepo.GetString()!;
                if (root.TryGetProperty("GiteaAccessToken", out var giteaToken) && !string.IsNullOrWhiteSpace(giteaToken.GetString()))
                    cfg.GiteaAccessToken = giteaToken.GetString()!;

                break;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Nelze načíst výchozí hodnoty instalátoru: {ex.Message}");
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataFolder);
            if (!string.IsNullOrWhiteSpace(DownloadFolder))
            {
                Directory.CreateDirectory(DownloadFolder);
            }
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(ConfigFilePath, json);
        }
        catch (Exception ex)
        {
            Logger.Error($"Chyba při ukládání konfigurace: {ex.Message}");
        }
    }

    public DateTime? GetCutoffDate()
    {
        return InitialMode switch
        {
            InitialSyncMode.OnlyNewFromNow => InstalledAt,
            InitialSyncMode.LastDays => DateTime.Today.AddDays(-InitialSyncDays),
            InitialSyncMode.FromDate => InitialSyncFromDate.Date,
            InitialSyncMode.AllHistory => null,
            _ => null
        };
    }
}

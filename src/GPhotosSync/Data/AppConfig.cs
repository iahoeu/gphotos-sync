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

    // Bezpečnostní identita složky, účtu a limit hromadného mazání (P0-1, P0-2, P0-3, P1-8)
    public string FolderId { get; set; } = "";
    public string ExpectedGoogleAccountEmail { get; set; } = "";
    public int MaxAutoDeletesPerCycle { get; set; } = 50;

    /// <summary>
    /// Podrobný protokol (kroky skenování a navigace) – jen pro ladění.
    /// </summary>
    public bool VerboseLogging { get; set; } = false;

    // Auto-Update Configuration (výchozí GitHub; lze nastavit vlastní Gitea/Forgejo server)
    public bool AutoCheckUpdates { get; set; } = true;
    public bool AutoInstallUpdates { get; set; } = false;
    public string GiteaServerUrl { get; set; } = "https://github.com";
    public string GiteaRepoOwner { get; set; } = "iahoeu";
    public string GiteaRepoName { get; set; } = "gphotos-sync";
    public string GiteaAccessToken { get; set; } = ""; // volitelné (v klientu pro veřejný repozitář nepoužíváno)

    [JsonIgnore]
    public bool HasConfigLoadError { get; set; }

    [JsonIgnore]
    public string? ConfigLoadErrorMessage { get; set; }

    public const string FolderMarkerFileName = ".gphotossync_folder_id";

    /// <summary>
    /// Postup obnovy, který se připojuje ke všem chybám identity cílové složky.
    /// </summary>
    public const string FolderRecoveryHint =
        " Postup: otevřete Nastavení, zvolte složku se staženými fotkami a uložte – aplikace ji ověří proti evidenci. " +
        "Pokud složka k evidenci nepatří, aplikace nabídne odpojení evidence (v Google Photos se nic nesmaže).";

    public static string AppDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GPhotosSync");

    public static string ConfigFilePath => Path.Combine(AppDataFolder, "config.json");

    /// <summary>
    /// Složka fotek se nesmí překrývat s datovou složkou aplikace (odinstalace ji může mazat).
    /// </summary>
    public static bool OverlapsAppDataFolder(string folder)
    {
        try
        {
            var a = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var b = Path.GetFullPath(AppDataFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }
    public static string WebViewProfileFolder => Path.Combine(AppDataFolder, "WebViewProfile");
    public static string DatabaseFilePath => Path.Combine(AppDataFolder, "sync_history.db");
    public static string LogFilePath => Path.Combine(AppDataFolder, "sync.log");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppConfig Load(string? customConfigPath = null, string? customDbPath = null)
    {
        var configPath = customConfigPath ?? ConfigFilePath;
        var backupPath = configPath + ".bak";
        try
        {
            var dir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (cfg != null)
                {
                    return cfg;
                }
                throw new InvalidDataException("Obsah konfiguračního souboru je prázdný nebo neplatný.");
            }

            // Bod 3: Pokud config.json chybí, ale v databázi už existuje evidence synchronizace (nebo existuje .bak),
            // NIKDY nespouštět automaticky novou instalaci, nevytvářet nový marker ani nepoužívat installer_defaults.json!
            bool hasExistingDb = SyncDatabase.HasExistingDatabaseWithRecords(customDbPath);
            bool hasBackup = File.Exists(backupPath);
            if (hasExistingDb || hasBackup)
            {
                var msg = hasBackup
                    ? "Konfigurační soubor config.json chybí (byla nalezena záloha config.json.bak a existující databáze). Synchronizace a mazání byly zastaveny – obnovte konfiguraci v Nastavení."
                    : "Konfigurační soubor config.json chybí, ale v databázi již existuje evidence synchronizace. Synchronizace a mazání byly zastaveny – ověřte složku v Nastavení.";
                Logger.Error(msg);
                return new AppConfig
                {
                    TwoWayDelete = false,
                    IsFirstRunCompleted = true,
                    HasConfigLoadError = true,
                    ConfigLoadErrorMessage = msg
                };
            }

            // Skutečně čistá nová instalace bez existující databáze a bez zálohy
            var defaultCfg = new AppConfig();
            ApplyInstallerDefaults(defaultCfg);
            defaultCfg.InitializeFolderMarker();
            defaultCfg.Save(configPath);
            return defaultCfg;
        }
        catch (Exception ex)
        {
            var errMsg = $"Chyba při načítání konfigurace ({configPath}): {ex.Message}";
            Logger.Error(errMsg);
            return new AppConfig
            {
                TwoWayDelete = false,
                IsFirstRunCompleted = true,
                HasConfigLoadError = true,
                ConfigLoadErrorMessage = $"Konfigurační soubor je poškozený nebo nečitelný ({ex.Message}). Synchronizace a mazání byly zastaveny."
            };
        }
    }

    public static bool HasBackupFile(string? customConfigPath = null)
    {
        var configPath = customConfigPath ?? ConfigFilePath;
        return File.Exists(configPath + ".bak");
    }

    /// <summary>
    /// Bod 3 & Bod 12: Obnoví konfiguraci ze záložního souboru config.json.bak.
    /// </summary>
    public bool TryRestoreFromBackup(out string? errorMessage, string? customConfigPath = null)
    {
        var configPath = customConfigPath ?? ConfigFilePath;
        var backupPath = configPath + ".bak";
        errorMessage = null;

        try
        {
            if (!File.Exists(backupPath))
            {
                errorMessage = $"Záložní soubor '{backupPath}' nebyl nalezen.";
                return false;
            }

            var json = File.ReadAllText(backupPath);
            var restored = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (restored == null || string.IsNullOrWhiteSpace(restored.DownloadFolder))
            {
                errorMessage = "Záložní soubor config.json.bak je neplatný nebo neobsahuje cílovou složku.";
                return false;
            }

            CopyFrom(restored);
            HasConfigLoadError = false;
            ConfigLoadErrorMessage = null;
            Save(configPath);
            Logger.Info($"Konfigurace byla úspěšně obnovena ze zálohy '{backupPath}'.");
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"Obnova ze zálohy selhala: {ex.Message}";
            Logger.Error(errorMessage);
            return false;
        }
    }

    /// <summary>
    /// Bod 12: Umožní uživateli v aplikaci opravit chybový stav konfigurace s ověřením proti existující složce a databázi.
    /// </summary>
    public void ClearConfigErrorAndSave(string? customConfigPath = null)
    {
        HasConfigLoadError = false;
        ConfigLoadErrorMessage = null;
        IsFirstRunCompleted = true;
        Save(customConfigPath);
    }

    private void CopyFrom(AppConfig other)
    {
        DownloadFolder = other.DownloadFolder;
        SyncIntervalMinutes = other.SyncIntervalMinutes;
        DownloadVideos = other.DownloadVideos;
        TwoWayDelete = other.TwoWayDelete;
        StartWithWindows = other.StartWithWindows;
        ShowNotifications = other.ShowNotifications;
        InitialMode = other.InitialMode;
        InitialSyncDays = other.InitialSyncDays;
        InitialSyncFromDate = other.InitialSyncFromDate;
        IsFirstRunCompleted = other.IsFirstRunCompleted;
        BaselineCaptured = other.BaselineCaptured;
        InstalledAt = other.InstalledAt;
        FolderId = other.FolderId;
        ExpectedGoogleAccountEmail = other.ExpectedGoogleAccountEmail;
        MaxAutoDeletesPerCycle = other.MaxAutoDeletesPerCycle;
        AutoCheckUpdates = other.AutoCheckUpdates;
        AutoInstallUpdates = other.AutoInstallUpdates;
        GiteaServerUrl = other.GiteaServerUrl;
        GiteaRepoOwner = other.GiteaRepoOwner;
        GiteaRepoName = other.GiteaRepoName;
        GiteaAccessToken = other.GiteaAccessToken;
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

    /// <summary>
    /// Vytvoří nebo obnoví identifikační marker soubor (.gphotossync_folder_id) ve zvolené cílové složce.
    /// Volá se pouze při prvotním nastavení nebo po vědomém potvrzení změny složky uživatelem.
    /// </summary>
    public bool InitializeFolderMarker(string? folderPath = null, bool generateNewId = false)
    {
        try
        {
            var target = (folderPath ?? DownloadFolder)?.Trim();
            if (string.IsNullOrWhiteSpace(target))
                return false;

            Directory.CreateDirectory(target);
            var markerPath = Path.Combine(target, FolderMarkerFileName);

            if (!generateNewId && File.Exists(markerPath))
            {
                var existingMarkerId = File.ReadAllText(markerPath).Trim();
                if (!string.IsNullOrWhiteSpace(existingMarkerId))
                {
                    FolderId = existingMarkerId;
                    return true;
                }
            }

            if (generateNewId || string.IsNullOrWhiteSpace(FolderId))
            {
                FolderId = Guid.NewGuid().ToString("N");
            }

            if (File.Exists(markerPath))
            {
                File.SetAttributes(markerPath, FileAttributes.Normal);
            }
            File.WriteAllText(markerPath, FolderId);
            File.SetAttributes(markerPath, FileAttributes.Hidden);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Nelze zapsat identifikační soubor cílové složky: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Ověří, že cílová složka existuje a její identifikační soubor (.gphotossync_folder_id) odpovídá konfiguraci.
    /// Nikdy nevytváří chybějící složku, pokud už v databázi existuje evidence stažených fotek (P0-1, P0-2).
    /// </summary>
    public (bool IsValid, string? ErrorMessage) VerifyDownloadFolderIdentity(
        bool hasExistingDownloadedRecords,
        Func<bool>? anyTrackedFileExistsOnDisk = null,
        string? customConfigPath = null)
    {
        if (HasConfigLoadError)
        {
            return (false, ConfigLoadErrorMessage ?? "Konfigurační soubor je poškozený.");
        }

        if (string.IsNullOrWhiteSpace(DownloadFolder))
        {
            return (false, "Cílová složka pro synchronizaci není nastavena.");
        }

        if (!Directory.Exists(DownloadFolder))
        {
            if (hasExistingDownloadedRecords)
            {
                return (false, $"Cílová složka '{DownloadFolder}' nebyla nalezena (odpojený disk nebo smazaná složka). Mazání a synchronizace byly zastaveny.");
            }

            // Žádné stažené položky v evidenci nejsou -> lze bezpečně inicializovat novou složku
            if (!InitializeFolderMarker(DownloadFolder))
            {
                return (false, $"Nelze vytvořit cílovou složku '{DownloadFolder}'.");
            }
            Save(customConfigPath);
            return (true, null);
        }

        var markerPath = Path.Combine(DownloadFolder, FolderMarkerFileName);
        string? diskMarkerId = null;
        if (File.Exists(markerPath))
        {
            try
            {
                diskMarkerId = File.ReadAllText(markerPath).Trim();
            }
            catch (Exception ex)
            {
                return (false, $"Nelze přečíst identifikační soubor složky '{markerPath}': {ex.Message}");
            }
        }

        // Hladký přechod ze starší verze (kdy ještě FolderId nebylo v configu)
        if (string.IsNullOrWhiteSpace(FolderId))
        {
            if (!string.IsNullOrWhiteSpace(diskMarkerId))
            {
                FolderId = diskMarkerId;
                Save(customConfigPath);
                return (true, null);
            }

            bool safeToAdopt = !hasExistingDownloadedRecords || (anyTrackedFileExistsOnDisk?.Invoke() ?? false);
            if (safeToAdopt)
            {
                if (!InitializeFolderMarker(DownloadFolder))
                {
                    return (false, $"Nelze inicializovat identitu složky '{DownloadFolder}'.");
                }
                Save(customConfigPath);
                return (true, null);
            }

            return (false, $"Ve složce '{DownloadFolder}' chybí identifikační soubor (.gphotossync_folder_id) i dříve stažené soubory. Mazání a synchronizace byly zastaveny." + FolderRecoveryHint);
        }

        if (string.IsNullOrWhiteSpace(diskMarkerId))
        {
            return (false, $"Ve složce '{DownloadFolder}' chybí identifikační soubor (.gphotossync_folder_id). Složka mohla být nahrazena prázdnou složkou – mazání je zablokováno." + FolderRecoveryHint);
        }

        if (!string.Equals(FolderId.Trim(), diskMarkerId, StringComparison.Ordinal))
        {
            return (false, $"Identita cílové složky '{DownloadFolder}' neodpovídá konfiguraci. Synchronizace a mazání jsou zastaveny." + FolderRecoveryHint);
        }

        return (true, null);
    }

    public void Save(string? customConfigPath = null)
    {
        if (HasConfigLoadError)
        {
            Logger.Warn("Uložení konfigurace přeskočeno, protože původní config.json je v chybovém stavu.");
            return;
        }

        var configPath = customConfigPath ?? ConfigFilePath;
        try
        {
            var dir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(this, JsonOptions);
            var tempPath = configPath + ".tmp";
            var backupPath = configPath + ".bak";

            File.WriteAllText(tempPath, json);
            if (File.Exists(configPath))
            {
                File.Replace(tempPath, configPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, configPath);
            }
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

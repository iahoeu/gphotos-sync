using System.Runtime.InteropServices;
using GPhotosSync.Data;

namespace GPhotosSync.Services;

public class SyncStatusEventArgs : EventArgs
{
    public string StatusText { get; set; } = string.Empty;
    public bool IsSyncing { get; set; }
    public bool RequiresLogin { get; set; }
    public bool HasError { get; set; }
    public int PendingBulkDeleteCount { get; set; }
    public bool IsDeletePausedDueToUnverifiedAccount { get; set; }
    public string? UnverifiedAccountReason { get; set; }
}

public class SyncEngine : IDisposable
{
    private const int MaxDownloadsPerCycle = 25;
    private const int PeriodicFullScanIntervalDays = 7;
    private const int IdleSecondsRequiredForFullScan = 600; // 10 minut nečinnosti PC

    public const string StateKeyFolderId = "FolderId";
    private const string StateKeyBaselineResume = "BaselineResumeMediaKey";
    private const string StateKeyTimelineResume = "TimelineResumeMediaKey";
    private const string StateKeyFullScanCursor = "FullScanCursorMediaKey";
    private const string StateKeyLastFullScanUtc = "LastFullScanCompletedAtUtc";

    private readonly AppConfig _config;
    private readonly SyncDatabase _db;
    private readonly GooglePhotosEngine _photosEngine;
    private readonly System.Windows.Forms.Timer _syncTimer;
    private readonly System.Windows.Forms.Timer _deleteDebounceTimer;
    private FileSystemWatcher? _watcher;

    private bool _isSyncing;
    private int _pendingBulkDeleteCount;
    private string? _blockedBulkDeleteBatchId;
    private bool _isDeletePausedDueToUnverifiedAccount;
    private string? _unverifiedAccountReason;
    private CancellationTokenSource? _currentSyncCts;
    private Task _activeOperationTask = Task.CompletedTask;

    public event EventHandler<SyncStatusEventArgs>? StatusChanged;
    public event Action<string, string, ToolTipIcon>? ShowNotification;

    public GooglePhotosEngine PhotosEngine => _photosEngine;
    public SyncDatabase Database => _db;
    public bool IsSyncing => _isSyncing;
    public int PendingBulkDeleteCount => _pendingBulkDeleteCount;
    public string? BlockedBulkDeleteBatchId => _blockedBulkDeleteBatchId;
    public bool IsDeletePausedDueToUnverifiedAccount => _isDeletePausedDueToUnverifiedAccount;
    public string? UnverifiedAccountReason => _unverifiedAccountReason;

    public SyncEngine(AppConfig config, SyncDatabase db, GooglePhotosEngine photosEngine)
    {
        _config = config;
        _db = db;
        _photosEngine = photosEngine;

        _syncTimer = new System.Windows.Forms.Timer();
        _syncTimer.Tick += async (_, _) => await RunSyncCycleAsync(manualTrigger: false);

        // Debounce timer (12 seconds) for local file deletions/moves so temporary save operations by photo editors are safe
        _deleteDebounceTimer = new System.Windows.Forms.Timer { Interval = 12000 };
        _deleteDebounceTimer.Tick += async (_, _) =>
        {
            _deleteDebounceTimer.Stop();
            await ProcessLocalDeletionsAsync();
        };

        RefreshBulkDeleteStateFromDatabase();
        RefreshAccountDeleteVerificationState();
        SetupFileSystemWatcher();
        UpdateTimerInterval();
    }

    private void RefreshBulkDeleteStateFromDatabase()
    {
        try
        {
            var pending = _db.GetItemsByStatus(SyncItemStatus.PendingRemoteDelete);
            var blocked = pending.Where(i => !string.IsNullOrWhiteSpace(i.BulkDeleteBatchId) && !i.BulkDeleteApproved).ToList();
            if (blocked.Count > 0)
            {
                _blockedBulkDeleteBatchId = blocked[0].BulkDeleteBatchId;
                _pendingBulkDeleteCount = blocked.Count(i => string.Equals(i.BulkDeleteBatchId, _blockedBulkDeleteBatchId, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                _blockedBulkDeleteBatchId = null;
                _pendingBulkDeleteCount = 0;
            }
        }
        catch
        {
            _blockedBulkDeleteBatchId = null;
            _pendingBulkDeleteCount = 0;
        }
    }

    private void RefreshAccountDeleteVerificationState()
    {
        if (!_config.TwoWayDelete)
        {
            _isDeletePausedDueToUnverifiedAccount = false;
            _unverifiedAccountReason = null;
        }
        else if (string.IsNullOrWhiteSpace(_config.ExpectedGoogleAccountEmail))
        {
            _isDeletePausedDueToUnverifiedAccount = true;
            _unverifiedAccountReason = "Mazání v Google Photos je vypnuté, dokud není ověřen a uložen e-mail účtu Google.";
        }
    }

    public void ConfirmGoogleAccountEmailManually(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return;

        var trimmed = email.Trim();
        _config.ExpectedGoogleAccountEmail = trimmed;
        _config.Save();
        _photosEngine.ConfirmCurrentSessionAccountEmail(trimmed);
        _isDeletePausedDueToUnverifiedAccount = false;
        _unverifiedAccountReason = null;
        Logger.Info($"Uživatel ručně potvrdil a uložil e-mail účtu Google Photos pro mazání: {trimmed}");
        RaiseStatus("Účet Google potvrzen – připraveno", isSyncing: _isSyncing);
    }

    /// <summary>
    /// Bod 4: Zruší případnou probíhající synchronizaci i mazání a počká na jejich úplné doběhnutí
    /// před změnou cílové složky.
    /// </summary>
    public async Task StopAndWaitForIdleAsync()
    {
        _photosEngine.InvokeOnUiThread(() =>
        {
            _syncTimer.Stop();
            _deleteDebounceTimer.Stop();
        });

        CancelCurrentSync();

        var taskToWait = _activeOperationTask;
        if (taskToWait != null && !taskToWait.IsCompleted)
        {
            try
            {
                await taskToWait;
            }
            catch
            {
                // Ignorovat výjimky ze zrušené úlohy
            }
        }

        int spin = 0;
        while (_isSyncing && spin < 100)
        {
            await Task.Delay(50);
            spin++;
        }
    }

    public void UpdateConfiguration()
    {
        RefreshBulkDeleteStateFromDatabase();
        RefreshAccountDeleteVerificationState();
        SetupFileSystemWatcher();
        UpdateTimerInterval();
    }

    private void UpdateTimerInterval()
    {
        _photosEngine.InvokeOnUiThread(() =>
        {
            _syncTimer.Stop();
            int minutes = Math.Clamp(_config.SyncIntervalMinutes, 1, 1440);
            _syncTimer.Interval = minutes * 60 * 1000;
            if (_config.IsFirstRunCompleted && !_config.HasConfigLoadError)
            {
                _syncTimer.Start();
            }
        });
    }

    private void SetupFileSystemWatcher()
    {
        try
        {
            _watcher?.Dispose();
            _watcher = null;

            if (_config.HasConfigLoadError || string.IsNullOrWhiteSpace(_config.DownloadFolder))
                return;

            // Bod 5 (P0): Nikdy automaticky nevytvářet chybějící složku, pokud již v DB existují
            // položky ve stavu Downloaded (0) NEBO PendingRemoteDelete (2).
            if (!Directory.Exists(_config.DownloadFolder))
            {
                if (_db.HasDownloadedOrPendingDeleteItems())
                {
                    Logger.Error($"Cílová složka '{_config.DownloadFolder}' neexistuje, přestože databáze eviduje stažené nebo ke smazání čekající položky. Sledování složky nebylo spuštěno.");
                    return;
                }
                _config.InitializeFolderMarker(_config.DownloadFolder);
                _config.Save();
            }

            if (!Directory.Exists(_config.DownloadFolder))
                return;

            _watcher = new FileSystemWatcher(_config.DownloadFolder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                IncludeSubdirectories = true,
                SynchronizingObject = _photosEngine.UiSyncObject,
                EnableRaisingEvents = true
            };

            _watcher.Renamed += OnLocalFileRenamed;
            _watcher.Deleted += OnLocalFileDeleted;
        }
        catch (Exception ex)
        {
            Logger.Error($"Nelze spustit sledování složky '{_config.DownloadFolder}': {ex.Message}");
        }
    }

    private static bool IsIgnoredWatcherFile(string? relativeOrFileName)
    {
        if (string.IsNullOrWhiteSpace(relativeOrFileName))
            return true;

        var name = Path.GetFileName(relativeOrFileName);
        return name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, AppConfig.FolderMarkerFileName, StringComparison.OrdinalIgnoreCase);
    }

    private void OnLocalFileRenamed(object sender, RenamedEventArgs e)
    {
        if (IsIgnoredWatcherFile(e.OldName) || IsIgnoredWatcherFile(e.Name))
            return;

        _db.UpdateLocalFileName(e.OldName!, e.Name!);
    }

    private void OnLocalFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (IsIgnoredWatcherFile(e.Name))
            return;

        if (!_config.TwoWayDelete || _config.HasConfigLoadError)
            return;

        var record = _db.GetByLocalFileName(e.Name!);
        if (record != null)
        {
            Logger.Info($"Zjištěno odebrání/přesunutí souboru '{e.Name}' ze složky PC. Čekám na dokončení operace před kontrolou...");
            _photosEngine.InvokeOnUiThread(() =>
            {
                _deleteDebounceTimer.Stop();
                _deleteDebounceTimer.Start();
            });
        }
    }

    /// <summary>
    /// P0-1 & P0-2: Ověří integritu konfigurace a identitu cílové složky (.gphotossync_folder_id).
    /// Pokud cokoliv nesedí, zastaví synchronizaci i mazání a nahlásí chybu.
    /// </summary>
    public bool VerifyFolderAndConfigSafety(out string? errorMessage)
    {
        return VerifyFolderAndConfigSafety(_config, _db, out errorMessage);
    }

    public static bool VerifyFolderAndConfigSafety(AppConfig config, SyncDatabase db, out string? errorMessage)
    {
        bool hasDownloaded = db.HasDownloadedOrPendingDeleteItems();

        bool AnyTrackedFileOnDisk()
        {
            if (string.IsNullOrWhiteSpace(config.DownloadFolder) || !Directory.Exists(config.DownloadFolder))
                return false;

            var items = db.GetItemsByStatus(SyncItemStatus.Downloaded);
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.LocalFileName)) continue;
                var full = Path.Combine(config.DownloadFolder, item.LocalFileName);
                if (File.Exists(full)) return true;
            }
            return false;
        }

        var check = config.VerifyDownloadFolderIdentity(hasDownloaded, AnyTrackedFileOnDisk);
        errorMessage = check.ErrorMessage;
        if (!check.IsValid)
            return false;

        // Identita složky uložená v databázi chrání evidenci i proti configu, který ukazuje na jinou
        // (např. dřívější) složku s vlastním platným markerem – typicky obnova ze zálohy config.json.bak.
        var storedFolderId = db.GetState(StateKeyFolderId);
        if (string.IsNullOrWhiteSpace(storedFolderId) || !hasDownloaded)
        {
            if (!string.Equals(storedFolderId, config.FolderId, StringComparison.Ordinal))
            {
                db.SetState(StateKeyFolderId, config.FolderId);
            }
            return true;
        }

        if (!string.Equals(storedFolderId, config.FolderId, StringComparison.Ordinal))
        {
            errorMessage = $"Cílová složka '{config.DownloadFolder}' neodpovídá složce, ke které patří evidence stažených fotek. Synchronizace a mazání byly zastaveny – zvolte správnou složku v Nastavení.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Bod 7 + Doplněk: Potvrdí pouze konkrétní trvale uloženou dávku (podle BatchId v DB).
    /// Nikdy nerozšíří potvrzení na další fotky.
    /// </summary>
    public async Task ApprovePendingBulkDeleteAsync()
    {
        RefreshBulkDeleteStateFromDatabase();
        if (string.IsNullOrWhiteSpace(_blockedBulkDeleteBatchId))
            return;

        int approvedCount = _db.ApproveBulkDeleteBatch(_blockedBulkDeleteBatchId);
        Logger.Info($"Uživatel potvrdil hromadné přesunutí dávky '{_blockedBulkDeleteBatchId}' ({approvedCount} položek) do koše Google Photos.");
        _pendingBulkDeleteCount = 0;
        await ProcessLocalDeletionsAsync();
    }

    /// <summary>
    /// Bod 7 + Doplněk: Zruší hromadné mazání pouze pro konkrétní zablokovanou dávku v DB.
    /// Položky se stavem Downloaded (Status 0) ani jiné dávky nejsou dotčeny.
    /// </summary>
    public void CancelPendingBulkDelete()
    {
        RefreshBulkDeleteStateFromDatabase();
        if (string.IsNullOrWhiteSpace(_blockedBulkDeleteBatchId))
            return;

        var batchId = _blockedBulkDeleteBatchId;
        int count = _db.CancelBulkDeleteBatch(batchId);
        RefreshBulkDeleteStateFromDatabase();
        Logger.Info($"Uživatel zrušil hromadné mazání dávky '{batchId}' ({count} lokálně odebraných položek bylo odpojeno od mazání v Google Photos).");
        RaiseStatus("Hromadné mazání zrušeno – fotky v Google Photos ponechány", isSyncing: false);
    }

    public Task ProcessLocalDeletionsAsync(CancellationToken ct = default)
    {
        if (!_config.TwoWayDelete || _isSyncing)
            return Task.CompletedTask;

        _activeOperationTask = ProcessLocalDeletionsCoreAsync(ct);
        return _activeOperationTask;
    }

    private async Task ProcessLocalDeletionsCoreAsync(CancellationToken ct)
    {
        if (!VerifyFolderAndConfigSafety(out var folderErr))
        {
            Logger.Error($"Mazání zablokováno: {folderErr}");
            RaiseStatus(folderErr ?? "Chyba ověření cílové složky", isSyncing: false, hasError: true);
            return;
        }

        try
        {
            _isSyncing = true;

            // Bod 8 + Doplněk: Mazání je povolené až po ověřeném a uloženém ExpectedGoogleAccountEmail
            var loginCheck = await _photosEngine.CheckLoginAndAccountAsync(_config.ExpectedGoogleAccountEmail);
            if (!loginCheck.IsLoggedIn)
            {
                Logger.Warn($"Mazání neprovedeno – účet Google není přihlášen nebo nesouhlasí: {loginCheck.ErrorMessage}");
                bool isMismatch = !string.IsNullOrWhiteSpace(_config.ExpectedGoogleAccountEmail) &&
                                  !string.IsNullOrWhiteSpace(loginCheck.DetectedEmail);
                RaiseStatus(
                    loginCheck.ErrorMessage ?? "Vyžadováno přihlášení k účtu Google",
                    isSyncing: false,
                    requiresLogin: !isMismatch,
                    hasError: isMismatch);
                return;
            }

            if (string.IsNullOrWhiteSpace(_config.ExpectedGoogleAccountEmail) &&
                !string.IsNullOrWhiteSpace(loginCheck.DetectedEmail))
            {
                _config.ExpectedGoogleAccountEmail = loginCheck.DetectedEmail;
                _config.Save();
                Logger.Info($"Spárován a uložen účet Google Photos: {loginCheck.DetectedEmail}");
                loginCheck.IsAccountVerifiedForDelete = true;
            }

            if (!loginCheck.IsAccountVerifiedForDelete || string.IsNullOrWhiteSpace(_config.ExpectedGoogleAccountEmail))
            {
                _isDeletePausedDueToUnverifiedAccount = true;
                _unverifiedAccountReason = loginCheck.DeleteBlockedReason ?? "Mazání je vypnuté – nepodařilo se ověřit e-mail účtu Google.";
                Logger.Warn($"Mazání zastaveno: {_unverifiedAccountReason}");
                RaiseStatus("Mazání pozastaveno – potvrďte účet Google v menu u ikony", isSyncing: false, hasError: true);
                return;
            }

            _isDeletePausedDueToUnverifiedAccount = false;
            _unverifiedAccountReason = null;

            await ExecuteVerifiedTwoWayDeletionsInternalAsync(ct);
        }
        catch (OperationCanceledException)
        {
            RaiseStatus("Mazání přerušeno", isSyncing: false);
        }
        catch (Exception ex)
        {
            Logger.Error($"Chyba při obousměrném mazání: {ex.Message}");
            RaiseStatus("Chyba při obousměrném mazání (viz protokol)", isSyncing: false, hasError: true);
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private async Task<(int TrashedCount, int FailedCount, bool BlockedByBulkSafeguard, int AmbiguousCount)> ExecuteVerifiedTwoWayDeletionsInternalAsync(CancellationToken ct)
    {
        var detectRes = DetectMissingLocalFilesForTwoWayDelete(_config, _db);
        if (!detectRes.SearchCompleted)
        {
            // Bez úplného prohledání složky nelze vyloučit, že "chybějící" soubory existují jinde -> nemazat nic,
            // ani položky, které už dříve čekaly na smazání.
            Logger.Error("Mazání v Google Photos zastaveno: kontrolu sledované složky se nepodařilo dokončit.");
            RaiseStatus("Mazání zastaveno – nelze ověřit obsah sledované složky (viz protokol)", isSyncing: false, hasError: true);
            return (0, 1, false, detectRes.AmbiguousSkippedCount);
        }

        int maxAuto = Math.Max(1, _config.MaxAutoDeletesPerCycle);
        var batchEval = _db.EvaluateOrLockBulkDeleteBatch(maxAuto);

        if (batchEval.IsBlocked)
        {
            bool notifyNow = _pendingBulkDeleteCount != batchEval.BlockedItems.Count ||
                             !string.Equals(_blockedBulkDeleteBatchId, batchEval.BlockedBatchId, StringComparison.OrdinalIgnoreCase);

            _blockedBulkDeleteBatchId = batchEval.BlockedBatchId;
            _pendingBulkDeleteCount = batchEval.BlockedItems.Count;

            var warnMsg = $"Zjištěno odebrání {_pendingBulkDeleteCount} fotek v dávce '{_blockedBulkDeleteBatchId}' (limit pojistky je {maxAuto}). Potvrďte prosím mazání u ikony v liště. Do rozhodnutí čekají i ostatní mazání v Google Photos.";
            Logger.Warn(warnMsg);
            RaiseStatus($"Čeká na potvrzení smazání {_pendingBulkDeleteCount} fotek (klikněte pravým na ikonu)", isSyncing: false, hasError: true);

            if (notifyNow)
            {
                ShowNotification?.Invoke(
                    "Pojistka hromadného mazání Google Photos",
                    $"V dávce čeká na smazání {_pendingBulkDeleteCount} souborů. Klikněte pravým tlačítkem na ikonu u hodin pro potvrzení nebo zrušení přesunu do koše. Do rozhodnutí čekají i ostatní mazání.",
                    ToolTipIcon.Warning);
            }

            return (0, 0, true, detectRes.AmbiguousSkippedCount);
        }

        _blockedBulkDeleteBatchId = null;
        _pendingBulkDeleteCount = 0;

        var pendingDeletes = batchEval.ReadyToDeleteItems;
        if (pendingDeletes.Count == 0)
        {
            return (0, 0, false, detectRes.AmbiguousSkippedCount);
        }

        RaiseStatus($"Přesouvám {pendingDeletes.Count} odebraných fotek do koše Google Photos...", isSyncing: true);

        int trashedCount = 0;
        int failedCount = 0;

        foreach (var item in pendingDeletes)
        {
            ct.ThrowIfCancellationRequested();

            // Znovu ověřit identitu složky před každou mazanou položkou
            if (!VerifyFolderAndConfigSafety(out var folderErr))
            {
                Logger.Error($"Mazání okamžitě zastaveno: {folderErr}");
                failedCount++;
                break;
            }

            // Double-check the file didn't reappear in folder or subfolders
            var localPath = Path.Combine(_config.DownloadFolder, item.LocalFileName);
            if (File.Exists(localPath))
            {
                _db.UpdateStatus(item.MediaKey, SyncItemStatus.Downloaded);
                continue;
            }

            bool ok = await _photosEngine.MoveToTrashAsync(item.MediaKey, item.PhotoUrl, ct);
            if (ok)
            {
                _db.UpdateStatus(item.MediaKey, SyncItemStatus.DeletedLocalAndRemote);
                trashedCount++;
            }
            else
            {
                failedCount++;
            }
        }

        if (trashedCount > 0)
        {
            Logger.Info($"Do koše v Google Photos bylo přesunuto {trashedCount} položek.");
            if (_config.ShowNotifications)
            {
                ShowNotification?.Invoke(
                    "Obousměrná synchronizace Google Photos",
                    $"Do koše v Google Photos bylo přesunuto {trashedCount} položek (smazaných/přesunutých na PC).",
                    ToolTipIcon.Info);
            }
        }

        return (trashedCount, failedCount, false, detectRes.AmbiguousSkippedCount);
    }

    /// <summary>
    /// Bod 1 (P0): Zjistí chybějící soubory, ale před zařazením ke smazání nejprve prohledá sledovanou složku
    /// i všechny její podsložky.
    /// - SHA-256 hash celého souboru + velikost má PŘEDNOST před názvem + velikostí.
    /// - Nejednoznačná shoda (víc kandidátů, chybí hash) = NEMAZAT a nahlásit.
    /// </summary>
    public static (int MissingMarkedCount, int AmbiguousSkippedCount, bool SearchCompleted) DetectMissingLocalFilesForTwoWayDelete(AppConfig config, SyncDatabase db)
    {
        if (!config.TwoWayDelete || config.HasConfigLoadError || string.IsNullOrWhiteSpace(config.DownloadFolder))
            return (0, 0, false);

        if (!VerifyFolderAndConfigSafety(config, db, out _))
            return (0, 0, false);

        var downloadedItems = db.GetItemsByStatus(SyncItemStatus.Downloaded);
        var pendingItems = db.GetItemsByStatus(SyncItemStatus.PendingRemoteDelete);

        var allCandidatesToCheck = new List<SyncItemRecord>();
        var activeKnownRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in downloadedItems)
        {
            if (string.IsNullOrWhiteSpace(item.LocalFileName))
                continue;

            var fullPath = Path.Combine(config.DownloadFolder, item.LocalFileName);
            if (File.Exists(fullPath))
            {
                activeKnownRelativePaths.Add(SyncDatabase.NormalizePathKey(item.LocalFileName));
            }
            else
            {
                allCandidatesToCheck.Add(item);
            }
        }

        foreach (var pending in pendingItems)
        {
            if (string.IsNullOrWhiteSpace(pending.LocalFileName))
                continue;

            var fullPath = Path.Combine(config.DownloadFolder, pending.LocalFileName);
            if (File.Exists(fullPath))
            {
                db.UpdateStatus(pending.MediaKey, SyncItemStatus.Downloaded);
                activeKnownRelativePaths.Add(SyncDatabase.NormalizePathKey(pending.LocalFileName));
            }
            else
            {
                allCandidatesToCheck.Add(pending);
            }
        }

        BackfillMissingHashes(db, downloadedItems, config.DownloadFolder);

        if (allCandidatesToCheck.Count == 0)
            return (0, 0, true);

        // Sestavit seznam všech neobsazených souborů ve sledované složce a jejích podsložkách
        var unmatchedDiskFiles = new List<(string FullPath, string RelativePath, string FileName, long Length, DateTime LastWriteTime, string? ComputedHash, DateTime? ComputedDate)>();
        try
        {
            foreach (var filePath in Directory.EnumerateFiles(config.DownloadFolder, "*", SearchOption.AllDirectories))
            {
                var relPath = SyncDatabase.NormalizePathKey(Path.GetRelativePath(config.DownloadFolder, filePath));
                if (IsIgnoredWatcherFile(relPath) || activeKnownRelativePaths.Contains(relPath))
                    continue;

                var fi = new FileInfo(filePath);
                unmatchedDiskFiles.Add((filePath, relPath, fi.Name, fi.Length, fi.LastWriteTime, null, null));
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Nelze prohledat podsložky '{config.DownloadFolder}' před mazáním: {ex.Message}");
            return (0, 0, false);
        }

        int missingMarkedCount = 0;
        int ambiguousSkippedCount = 0;

        // Položky s uloženým hashem zpracovat jako první: shoda hashe je jediný důkaz totožnosti
        // a soubor, který si takto přivlastní, už nemůže zablokovat ani přiřadit jiný záznam.
        var orderedCandidates = allCandidatesToCheck
            .OrderBy(x => string.IsNullOrEmpty(x.FileHash) ? 1 : 0)
            .ToList();

        foreach (var item in orderedCandidates)
        {
            var expectedFileName = Path.GetFileName(item.LocalFileName);
            int matchedIndex = -1;
            bool isAmbiguous = false;
            string ambiguousReason = "";

            if (!string.IsNullOrEmpty(item.FileHash))
            {
                // Přiřazení výhradně podle shody SHA-256 hashe celého souboru
                var hashMatches = new List<int>();
                bool anyHashUnreadable = false;
                for (int i = 0; i < unmatchedDiskFiles.Count; i++)
                {
                    var cand = unmatchedDiskFiles[i];
                    if (item.FileSize > 0 && cand.Length != item.FileSize)
                        continue;

                    var hash = cand.ComputedHash ?? ExifService.ComputeFastFileHash(cand.FullPath);
                    unmatchedDiskFiles[i] = cand with { ComputedHash = hash };

                    if (string.IsNullOrEmpty(hash))
                    {
                        // Soubor nelze přečíst (např. zamčený jiným programem) -> jeho identitu nelze ověřit
                        anyHashUnreadable = true;
                        continue;
                    }

                    if (string.Equals(hash, item.FileHash, StringComparison.OrdinalIgnoreCase))
                    {
                        hashMatches.Add(i);
                    }
                }

                if (hashMatches.Count == 1)
                {
                    matchedIndex = hashMatches[0];
                }
                else if (hashMatches.Count > 1)
                {
                    // Více identických kopií téhož souboru (shodný SHA-256): upřednostnit shodný název, jinak první kopii
                    int sameNameIdx = hashMatches.FirstOrDefault(idx =>
                        string.Equals(unmatchedDiskFiles[idx].FileName, expectedFileName, StringComparison.OrdinalIgnoreCase), -1);
                    matchedIndex = sameNameIdx >= 0 ? sameNameIdx : hashMatches[0];
                }
                else if (anyHashUnreadable)
                {
                    isAmbiguous = true;
                    ambiguousReason = "některý soubor shodné velikosti nelze přečíst a ověřit jeho hash";
                }
            }
            else
            {
                // Položka nemá uložený hash: název ani velikost nejsou důkazem totožnosti.
                // Existuje-li jakýkoliv soubor shodné velikosti, nelze vyloučit, že jde o tuto fotku -> NEMAZAT
                // a soubor ani nepřiřazovat (mohl by patřit jinému záznamu).
                bool anyPossibleCandidate = unmatchedDiskFiles.Any(cand => item.FileSize <= 0 || cand.Length == item.FileSize);
                if (anyPossibleCandidate)
                {
                    isAmbiguous = true;
                    ambiguousReason = "v DB chybí SHA-256 hash a ve složce existuje soubor shodné velikosti";
                }
            }

            if (isAmbiguous)
            {
                ambiguousSkippedCount++;
                if (item.Status == SyncItemStatus.PendingRemoteDelete)
                {
                    db.UpdateStatus(item.MediaKey, SyncItemStatus.Downloaded);
                }
                Logger.Warn($"Nelze ověřit identitu chybějícího souboru '{item.LocalFileName}' (MediaKey: {item.MediaKey}): {ambiguousReason}. Položka se z bezpečnostních důvodů NEMAŽE.");
                continue;
            }

            if (matchedIndex >= 0)
            {
                var matched = unmatchedDiskFiles[matchedIndex];
                unmatchedDiskFiles.RemoveAt(matchedIndex);
                activeKnownRelativePaths.Add(matched.RelativePath);

                var newHash = matched.ComputedHash ?? (string.IsNullOrEmpty(item.FileHash) ? ExifService.ComputeFastFileHash(matched.FullPath) : item.FileHash);
                db.UpdateLocalFileLocationAndHash(item.MediaKey, matched.RelativePath, newHash);
                Logger.Info($"Soubor '{item.LocalFileName}' byl nalezen jako přesunutý/přejmenovaný v cílové složce ('{matched.RelativePath}') -> v Google Photos se nemaže.");
                continue;
            }

            if (item.Status == SyncItemStatus.Downloaded)
            {
                // Ignore if downloaded less than 10 seconds ago
                if ((DateTime.Now - item.UpdatedAt).TotalSeconds < 10)
                    continue;

                Logger.Info($"Soubor '{item.LocalFileName}' již není v synchronizované složce (ani v podsložkách) -> zařazuji k přesunu do koše Google Photos.");
                db.UpdateStatus(item.MediaKey, SyncItemStatus.PendingRemoteDelete);
                missingMarkedCount++;
            }
        }

        return (missingMarkedCount, ambiguousSkippedCount, true);
    }

    private const int MaxHashBackfillPerCycle = 100;
    private static readonly TimeSpan MaxHashBackfillDuration = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Dopočítá chybějící SHA-256 hash u stažených souborů, které jsou stále na disku (záznamy ze starších verzí).
    /// Bez hashe nelze přejmenovaný soubor bezpečně rozpoznat, proto se hash doplňuje, dokud soubor existuje.
    /// </summary>
    private static void BackfillMissingHashes(SyncDatabase db, List<SyncItemRecord> downloadedItems, string downloadFolder)
    {
        int computed = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var item in downloadedItems)
        {
            // Omezit počet i čas, aby doplňování (např. u velkých videí) nezdrželo cyklus ani UI
            if (computed >= MaxHashBackfillPerCycle || sw.Elapsed > MaxHashBackfillDuration)
                break;
            if (!string.IsNullOrEmpty(item.FileHash) || string.IsNullOrWhiteSpace(item.LocalFileName))
                continue;

            var fullPath = Path.Combine(downloadFolder, item.LocalFileName);
            if (!File.Exists(fullPath))
                continue;

            var hash = ExifService.ComputeFastFileHash(fullPath);
            computed++;
            if (!string.IsNullOrEmpty(hash))
            {
                db.UpdateLocalFileLocationAndHash(item.MediaKey, item.LocalFileName, hash);
                item.FileHash = hash;
            }
        }

        if (computed > 0)
        {
            Logger.Info($"Doplněn SHA-256 hash u {computed} dříve stažených souborů.");
        }
    }

    public Task RunSyncCycleAsync(bool manualTrigger)
    {
        if (_isSyncing)
        {
            if (manualTrigger)
            {
                Logger.Info("Synchronizace již právě probíhá.");
            }
            return _activeOperationTask;
        }

        _activeOperationTask = RunSyncCycleCoreAsync(manualTrigger);
        return _activeOperationTask;
    }

    private async Task RunSyncCycleCoreAsync(bool manualTrigger)
    {
        // P0-1 & P0-2: Kontrola nepoškozeného configu a identity cílové složky před spuštěním cyklu
        if (!VerifyFolderAndConfigSafety(out var safetyError))
        {
            Logger.Error($"Synchronizace zastavena: {safetyError}");
            RaiseStatus(safetyError ?? "Chyba cílové složky", isSyncing: false, hasError: true);
            if (manualTrigger)
            {
                ShowNotification?.Invoke("Synchronizace zastavena", safetyError ?? "Chyba cílové složky", ToolTipIcon.Error);
            }
            return;
        }

        _isSyncing = true;
        _currentSyncCts = new CancellationTokenSource();
        var ct = _currentSyncCts.Token;

        // Bod 4: Zapamatovat si cílovou složku a její FolderId na začátku cyklu
        string cycleDownloadFolder = _config.DownloadFolder;
        string cycleFolderId = _config.FolderId;

        try
        {
            RaiseStatus("Kontroluji přihlášení ke Google Photos...", isSyncing: true);
            var loginRes = await _photosEngine.CheckLoginAndAccountAsync(_config.ExpectedGoogleAccountEmail);
            if (!loginRes.IsLoggedIn)
            {
                Logger.Warn($"Synchronizace neprovedena: {loginRes.ErrorMessage}");
                bool isAccountMismatch = !string.IsNullOrWhiteSpace(_config.ExpectedGoogleAccountEmail) &&
                                         !string.IsNullOrWhiteSpace(loginRes.DetectedEmail);
                RaiseStatus(
                    loginRes.ErrorMessage ?? "Vyžadováno přihlášení k účtu Google",
                    isSyncing: false,
                    requiresLogin: !isAccountMismatch,
                    hasError: isAccountMismatch);

                if (manualTrigger)
                {
                    ShowNotification?.Invoke(
                        isAccountMismatch ? "Nesouhlasí účet Google" : "Vyžadováno přihlášení",
                        loginRes.ErrorMessage ?? "Klikněte na ikonu aplikace u hodin a přihlaste se ke Google Photos.",
                        ToolTipIcon.Warning);
                }
                return;
            }

            // Bod 8 + Doplněk: Pokud byl e-mail účtu zjištěn ze stránky a ještě nebyl uložen, uložíme jej
            if (string.IsNullOrWhiteSpace(_config.ExpectedGoogleAccountEmail) &&
                !string.IsNullOrWhiteSpace(loginRes.DetectedEmail))
            {
                _config.ExpectedGoogleAccountEmail = loginRes.DetectedEmail;
                _config.Save();
                loginRes.IsAccountVerifiedForDelete = true;
                Logger.Info($"Spárován a uložen účet Google Photos: {loginRes.DetectedEmail}");
            }

            int failedDeletes = 0;
            int ambiguousDeletes = 0;
            bool blockedByBulkSafeguard = false;

            // 1. Nejprve zpracovat obousměrné mazání (pouze pokud je e-mail účtu Google ověřen a uložen!)
            if (_config.TwoWayDelete)
            {
                if (loginRes.IsAccountVerifiedForDelete && !string.IsNullOrWhiteSpace(_config.ExpectedGoogleAccountEmail))
                {
                    _isDeletePausedDueToUnverifiedAccount = false;
                    _unverifiedAccountReason = null;

                    var delResult = await ExecuteVerifiedTwoWayDeletionsInternalAsync(ct);
                    failedDeletes = delResult.FailedCount;
                    ambiguousDeletes = delResult.AmbiguousCount;
                    blockedByBulkSafeguard = delResult.BlockedByBulkSafeguard;
                }
                else
                {
                    _isDeletePausedDueToUnverifiedAccount = true;
                    _unverifiedAccountReason = loginRes.DeleteBlockedReason
                        ?? "Mazání v Google Photos je pozastaveno – e-mail účtu Google nebyl ověřen (potvrďte účet v menu u ikony).";
                    Logger.Warn(_unverifiedAccountReason);
                }
            }
            else
            {
                _isDeletePausedDueToUnverifiedAccount = false;
                _unverifiedAccountReason = null;
            }

            // 2. Skenování Google Photos
            RaiseStatus("Hledám nové fotky a videa v Google Photos...", isSyncing: true);

            DateTime? cutoffDate = _config.InitialMode == InitialSyncMode.OnlyNewFromNow
                ? null
                : _config.GetCutoffDate();

            bool isInitialBaselineCapture = _config.InitialMode == InitialSyncMode.OnlyNewFromNow && !_config.BaselineCaptured;
            bool scanAllHistory = _config.InitialMode == InitialSyncMode.AllHistory;

            if (isInitialBaselineCapture)
            {
                string? baselineResumeKey = _db.GetState(StateKeyBaselineResume);
                var baselineScan = await _photosEngine.ScanTimelineAsync(
                    isAlreadyKnownMediaKey: null,
                    cutoffDate: null,
                    scanAllHistory: true,
                    maxScrollStepsLimit: 60,
                    resumeAfterMediaKey: baselineResumeKey,
                    ct: ct);

                if (!baselineScan.Success)
                {
                    Logger.Error($"Skenování výchozího stavu selhalo: {baselineScan.ErrorMessage}");
                    RaiseStatus($"Chyba čtení Google Photos: {baselineScan.ErrorMessage}", isSyncing: false, hasError: true);
                    return;
                }

                foreach (var item in baselineScan.Items)
                {
                    // Fotky ze dne instalace a novější do výchozího stavu nepatří: zaznamenávání může běžet
                    // přes více cyklů a fotka pořízená mezitím by se jinak už nikdy nestáhla.
                    if (item.EstimatedDate.HasValue && item.EstimatedDate.Value.Date >= _config.InstalledAt.Date)
                        continue;

                    if (!_db.Exists(item.MediaKey))
                    {
                        _db.UpsertItem(new SyncItemRecord
                        {
                            MediaKey = item.MediaKey,
                            PhotoUrl = item.PhotoUrl,
                            LocalFileName = string.Empty,
                            FileSize = 0,
                            TakenAt = item.EstimatedDate,
                            UpdatedAt = DateTime.Now,
                            Status = SyncItemStatus.SkippedInitial,
                            SkipReason = SkipReason.Baseline
                        });
                    }
                }

                // Bod 10: BaselineCaptured nastavit na true POUZE tehdy, když sken skutečně dosáhl konce knihovny
                if (baselineScan.ReachedTargetBoundary)
                {
                    _db.SetState(StateKeyBaselineResume, null);
                    _config.BaselineCaptured = true;
                    _config.Save();
                    Logger.Info($"Výchozí stav kompletně zaznamenán ({baselineScan.Items.Count} položek v posledním kroku). Od této chvíle se budou stahovat nově přidané fotky a videa.");
                    RaiseStatus("Připraveno – čekám na nové fotky z telefonu", isSyncing: false, hasError: blockedByBulkSafeguard || failedDeletes > 0 || _isDeletePausedDueToUnverifiedAccount);
                }
                else
                {
                    string? lastKey = baselineScan.Items.LastOrDefault()?.MediaKey;
                    _db.SetState(StateKeyBaselineResume, lastKey);
                    Logger.Info($"Zaznamenávání výchozího stavu ještě nedosáhlo konce knihovny (zpracováno {baselineScan.Items.Count} položek) – bude pokračovat v dalším cyklu.");

                    // Do dokončení výchozího stavu stahovat jen fotky ze dne instalace a novější,
                    // aby nové fotky nemusely čekat a staré se omylem nestáhly.
                    cutoffDate = _config.InstalledAt.Date;
                }
            }

            bool baselineIncomplete = isInitialBaselineCapture && !_config.BaselineCaptured;

            var discoveredByKey = new Dictionary<string, DiscoveredMediaItem>(StringComparer.Ordinal);
            string? timelineResumeKey = _db.GetState(StateKeyTimelineResume);

            // Bod 10b: Přednostně skenovat pohled „Nedávno přidané“ (/search/_tra_), který je řazen podle data nahrání,
            // aby se zachytily i nově nahrané fotky se starým EXIF datem pořízení.
            if (string.IsNullOrWhiteSpace(timelineResumeKey) && !baselineIncomplete)
            {
                var recentAddedScan = await _photosEngine.ScanRecentlyAddedAsync(
                    isAlreadyKnownMediaKey: _db.Exists,
                    maxScrollStepsLimit: 12,
                    ct: ct);

                if (recentAddedScan.Success)
                {
                    foreach (var item in recentAddedScan.Items)
                    {
                        discoveredByKey[item.MediaKey] = item;
                    }
                }
                else
                {
                    Logger.Warn($"Pohled 'Nedávno přidané' se nepodařilo přečíst ({recentAddedScan.ErrorMessage}) – nově nahrané fotky se starším datem pořízení se v tomto cyklu nezachytí.");
                }
            }

            // Následně skenovat hlavní časovou osu (s případným navázáním na rozpracovaný sken z minulého cyklu)
            int maxScrollStepsLimit = string.IsNullOrWhiteSpace(timelineResumeKey)
                ? (scanAllHistory ? 45 : 35)
                : 45;

            var scanResult = await _photosEngine.ScanTimelineAsync(
                isAlreadyKnownMediaKey: _db.Exists,
                cutoffDate: cutoffDate,
                scanAllHistory: scanAllHistory,
                maxScrollStepsLimit: maxScrollStepsLimit,
                resumeAfterMediaKey: timelineResumeKey,
                ct: ct);

            if (!scanResult.Success)
            {
                Logger.Error($"Skenování časové osy selhalo: {scanResult.ErrorMessage}");
                RaiseStatus($"Chyba čtení Google Photos: {scanResult.ErrorMessage}", isSyncing: false, hasError: true);
                return;
            }

            foreach (var item in scanResult.Items)
            {
                if (!discoveredByKey.ContainsKey(item.MediaKey))
                {
                    discoveredByKey[item.MediaKey] = item;
                }
            }

            // Bod 10b & 10c: Záložní periodický úplný průchod (1x týdně mimo aktivní používání PC),
            // rozdělený do malých dávek, aby neblokoval mazání ani běžné stahování.
            bool ranFullScanBatch = false;
            bool fullScanIncomplete = false;
            if (!baselineIncomplete && scanResult.ReachedTargetBoundary && string.IsNullOrWhiteSpace(timelineResumeKey) && ShouldRunPeriodicFullScanBatch(manualTrigger))
            {
                ranFullScanBatch = true;
                string? fullScanCursor = _db.GetState(StateKeyFullScanCursor);
                Logger.Info($"Spouštím dávku periodické úplné kontroly knihovny (od pozice: {fullScanCursor ?? "začátek"})...");

                var fullScanBatch = await _photosEngine.ScanTimelineAsync(
                    isAlreadyKnownMediaKey: null, // Při úplném průchodu nezastavovat na známých položkách
                    cutoffDate: cutoffDate,
                    scanAllHistory: true,
                    maxScrollStepsLimit: 25,
                    resumeAfterMediaKey: fullScanCursor,
                    ct: ct);

                if (fullScanBatch.Success)
                {
                    foreach (var item in fullScanBatch.Items)
                    {
                        if (!discoveredByKey.ContainsKey(item.MediaKey))
                        {
                            discoveredByKey[item.MediaKey] = item;
                        }
                    }

                    if (fullScanBatch.ReachedTargetBoundary)
                    {
                        _db.SetState(StateKeyFullScanCursor, null);
                        _db.SetState(StateKeyLastFullScanUtc, DateTime.UtcNow.ToString("O"));
                        Logger.Info("Periodická úplná kontrola knihovny Google Photos byla dokončena.");
                    }
                    else
                    {
                        fullScanIncomplete = true;
                        string? nextCursor = fullScanBatch.Items.LastOrDefault()?.MediaKey;
                        if (!string.IsNullOrWhiteSpace(nextCursor))
                        {
                            _db.SetState(StateKeyFullScanCursor, nextCursor);
                        }
                    }
                }
            }

            var discovered = discoveredByKey.Values.ToList();
            Logger.Info($"Nalezeno {discovered.Count} položek ke kontrole z Google Photos.");

            var newItems = new List<DiscoveredMediaItem>();
            foreach (var item in discovered)
            {
                if (_db.Exists(item.MediaKey))
                    continue;

                if (!_config.DownloadVideos && item.IsVideo)
                {
                    _db.UpsertItem(new SyncItemRecord
                    {
                        MediaKey = item.MediaKey,
                        PhotoUrl = item.PhotoUrl,
                        LocalFileName = string.Empty,
                        TakenAt = item.EstimatedDate,
                        UpdatedAt = DateTime.Now,
                        Status = SyncItemStatus.SkippedInitial,
                        SkipReason = SkipReason.VideoDisabled
                    });
                    continue;
                }

                if (cutoffDate.HasValue && item.EstimatedDate.HasValue && item.EstimatedDate.Value.Date < cutoffDate.Value.Date)
                {
                    Logger.Info($"Přeskakuji starší položku '{item.MediaKey}' (datum na časové ose {item.EstimatedDate.Value:dd.MM.yyyy} je starší než limit {cutoffDate.Value:dd.MM.yyyy}).");
                    _db.UpsertItem(new SyncItemRecord
                    {
                        MediaKey = item.MediaKey,
                        PhotoUrl = item.PhotoUrl,
                        LocalFileName = string.Empty,
                        TakenAt = item.EstimatedDate,
                        UpdatedAt = DateTime.Now,
                        Status = SyncItemStatus.SkippedInitial,
                        SkipReason = SkipReason.CutoffDate
                    });
                    continue;
                }

                newItems.Add(item);
            }

            // Bod 10c: Omezit počet stahování v jednom cyklu na dávku, aby cyklus neblokoval mazání ani odezvu aplikace
            bool downloadBatchTruncated = newItems.Count > MaxDownloadsPerCycle;
            var itemsToDownloadThisCycle = downloadBatchTruncated
                ? newItems.Take(MaxDownloadsPerCycle).ToList()
                : newItems;

            int downloadedCount = 0;
            int failedDownloads = 0;
            string? lastProcessedMediaKey = null;

            if (itemsToDownloadThisCycle.Count == 0)
            {
                Logger.Info("Žádné nové položky ke stažení v tomto cyklu.");
            }
            else
            {
                Logger.Info($"Zahajuji stahování dávky {itemsToDownloadThisCycle.Count} (z {newItems.Count}) nových položek do '{cycleDownloadFolder}'...");

                for (int i = 0; i < itemsToDownloadThisCycle.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    // Bod 4: Před každým stahováním ověřit, že se cílová složka nezměnila
                    string? preDlErr = null;
                    if (!IsSameTargetFolder(cycleDownloadFolder, cycleFolderId) || !VerifyFolderAndConfigSafety(out preDlErr))
                    {
                        Logger.Warn($"Stahování dávky zastaveno – cílová složka byla změněna nebo neprošla kontrolou ({preDlErr}).");
                        break;
                    }

                    var item = itemsToDownloadThisCycle[i];

                    cutoffDate = _config.InitialMode == InitialSyncMode.OnlyNewFromNow
                        ? (baselineIncomplete ? _config.InstalledAt.Date : null)
                        : _config.GetCutoffDate();

                    if (cutoffDate.HasValue && item.EstimatedDate.HasValue && item.EstimatedDate.Value.Date < cutoffDate.Value.Date)
                    {
                        Logger.Info($"Přeskakuji starší položku '{item.MediaKey}' podle nově upraveného nastavení ({item.EstimatedDate.Value:dd.MM.yyyy} < {cutoffDate.Value:dd.MM.yyyy}).");
                        _db.UpsertItem(new SyncItemRecord
                        {
                            MediaKey = item.MediaKey,
                            PhotoUrl = item.PhotoUrl,
                            LocalFileName = string.Empty,
                            TakenAt = item.EstimatedDate,
                            UpdatedAt = DateTime.Now,
                            Status = SyncItemStatus.SkippedInitial,
                            SkipReason = SkipReason.CutoffDate
                        });
                        lastProcessedMediaKey = item.MediaKey;
                        continue;
                    }

                    RaiseStatus($"Stahuji ({i + 1}/{itemsToDownloadThisCycle.Count})...", isSyncing: true);

                    var res = await _photosEngine.DownloadMediaAsync(
                        item,
                        cycleDownloadFolder,
                        _config.DownloadVideos,
                        ct);

                    // Bod 4: Výsledek stahování zapsat do DB jen tehdy, pokud cílová složka stále odpovídá složce, do které se stahovalo
                    string? postDlErr = null;
                    if (!IsSameTargetFolder(cycleDownloadFolder, cycleFolderId) || !VerifyFolderAndConfigSafety(out postDlErr))
                    {
                        if (!string.IsNullOrWhiteSpace(res.FullPath) && File.Exists(res.FullPath))
                        {
                            try { File.Delete(res.FullPath); } catch { }
                        }
                        Logger.Warn($"Zápis staženého souboru '{res.LocalFileName}' zrušen – cílová složka se během stahování změnila ({postDlErr}).");
                        break;
                    }

                    if (res.SkippedVideo)
                    {
                        _db.UpsertItem(new SyncItemRecord
                        {
                            MediaKey = item.MediaKey,
                            PhotoUrl = item.PhotoUrl,
                            LocalFileName = res.LocalFileName,
                            TakenAt = item.EstimatedDate,
                            UpdatedAt = DateTime.Now,
                            Status = SyncItemStatus.SkippedInitial,
                            SkipReason = SkipReason.VideoDisabled
                        });
                        lastProcessedMediaKey = item.MediaKey;
                        continue;
                    }

                    if (res.Success)
                    {
                        bool timelineConfirmedRecent = item.EstimatedDate.HasValue &&
                                                       cutoffDate.HasValue &&
                                                       item.EstimatedDate.Value.Date >= cutoffDate.Value.Date;

                        if (!timelineConfirmedRecent &&
                            cutoffDate.HasValue &&
                            res.TakenAt.HasValue &&
                            res.TakenAt.Value.Date < cutoffDate.Value.Date)
                        {
                            Logger.Info($"Přeskakuji stažený soubor '{res.LocalFileName}' – EXIF datum pořízení ({res.TakenAt.Value:dd.MM.yyyy}) je starší než zvolený limit ({cutoffDate.Value:dd.MM.yyyy}).");
                            try { File.Delete(res.FullPath); } catch { }
                            _db.UpsertItem(new SyncItemRecord
                            {
                                MediaKey = item.MediaKey,
                                PhotoUrl = item.PhotoUrl,
                                LocalFileName = string.Empty,
                                TakenAt = res.TakenAt,
                                UpdatedAt = DateTime.Now,
                                Status = SyncItemStatus.SkippedInitial,
                                SkipReason = SkipReason.CutoffDate
                            });
                            lastProcessedMediaKey = item.MediaKey;
                            continue;
                        }

                        _db.UpsertItem(new SyncItemRecord
                        {
                            MediaKey = item.MediaKey,
                            PhotoUrl = item.PhotoUrl,
                            LocalFileName = res.LocalFileName,
                            FileSize = res.FileSize,
                            FileHash = res.FileHash,
                            TakenAt = res.TakenAt ?? item.EstimatedDate,
                            UpdatedAt = DateTime.Now,
                            Status = SyncItemStatus.Downloaded,
                            SkipReason = SkipReason.None
                        });

                        downloadedCount++;
                        lastProcessedMediaKey = item.MediaKey;
                        Logger.Info($"Staženo: {res.LocalFileName} ({res.FileSize / 1024:N0} KB)");
                    }
                    else
                    {
                        failedDownloads++;
                        Logger.Warn($"Nepodařilo se stáhnout položku '{item.MediaKey}': {res.ErrorMessage}");
                    }
                }
            }

            // Bod 10a: Uložit nebo vyčistit pozici rozpracovaného skenu mezi cykly
            bool hasMoreTimelineWork = !scanResult.ReachedTargetBoundary || downloadBatchTruncated;
            if (hasMoreTimelineWork && failedDownloads == 0)
            {
                string? resumeAnchor = downloadBatchTruncated
                    ? lastProcessedMediaKey
                    : scanResult.Items.LastOrDefault()?.MediaKey;
                _db.SetState(StateKeyTimelineResume, resumeAnchor);
            }
            else if (scanResult.ReachedTargetBoundary && !downloadBatchTruncated && failedDownloads == 0)
            {
                _db.SetState(StateKeyTimelineResume, null);
                if (!_config.BaselineCaptured)
                {
                    _config.BaselineCaptured = true;
                    _config.Save();
                }
            }

            if (downloadedCount > 0 && _config.ShowNotifications)
            {
                ShowNotification?.Invoke(
                    "Staženy nové fotky z Google Photos",
                    $"Do složky '{Path.GetFileName(cycleDownloadFolder)}' bylo staženo {downloadedCount} nových položek.",
                    ToolTipIcon.Info);
            }

            // Neskrývat chyby, neověřený účet ani neúplný sken pod hláškou "Synchronizováno"
            if (blockedByBulkSafeguard)
            {
                RaiseStatus($"Čeká na potvrzení smazání {_pendingBulkDeleteCount} fotek (klikněte pravým na ikonu)", isSyncing: false, hasError: true);
            }
            else if (_isDeletePausedDueToUnverifiedAccount)
            {
                RaiseStatus("Stahování OK – mazání vypnuto (potvrďte účet Google v menu)", isSyncing: false, hasError: true);
            }
            else if (failedDeletes > 0 || failedDownloads > 0 || ambiguousDeletes > 0)
            {
                RaiseStatus($"Dokončeno s upozorněním: chyby stahování ({failedDownloads}), mazání ({failedDeletes}), nejednoznačné ({ambiguousDeletes})", isSyncing: false, hasError: true);
            }
            else if (hasMoreTimelineWork || (ranFullScanBatch && fullScanIncomplete))
            {
                Logger.Info("Časová osa obsahuje další položky – skenování bude pokračovat v dalším cyklu.");
                RaiseStatus($"Stažena dávka ({DateTime.Now:HH:mm}) – pokračování v dalším cyklu", isSyncing: false);
            }
            else
            {
                RaiseStatus($"Synchronizováno ({DateTime.Now:HH:mm})", isSyncing: false);
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Info("Synchronizace byla přerušena.");
            RaiseStatus("Synchronizace přerušena", isSyncing: false);
        }
        catch (Exception ex)
        {
            Logger.Error($"Chyba při synchronizaci: {ex.Message}");
            RaiseStatus("Chyba při synchronizaci (podrobnosti v protokolu)", isSyncing: false, hasError: true);
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private bool IsSameTargetFolder(string expectedFolder, string expectedFolderId)
    {
        return string.Equals(_config.DownloadFolder, expectedFolder, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(_config.FolderId, expectedFolderId, StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldRunPeriodicFullScanBatch(bool manualTrigger)
    {
        if (manualTrigger)
            return false;

        // Pokud již je rozpracovaný úplný průchod a PC je v klidu, pokračovat v další dávce
        string? cursor = _db.GetState(StateKeyFullScanCursor);
        uint idleSec = GetUserIdleSeconds();
        if (idleSec < IdleSecondsRequiredForFullScan)
            return false;

        if (!string.IsNullOrWhiteSpace(cursor))
            return true;

        string? lastUtcStr = _db.GetState(StateKeyLastFullScanUtc);
        if (string.IsNullOrWhiteSpace(lastUtcStr) || !DateTime.TryParse(lastUtcStr, out var lastUtc))
            return true;

        return (DateTime.UtcNow - lastUtc.ToUniversalTime()).TotalDays >= PeriodicFullScanIntervalDays;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private static uint GetUserIdleSeconds()
    {
        try
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (GetLastInputInfo(ref lii))
            {
                uint elapsedTicks = unchecked((uint)Environment.TickCount - lii.dwTime);
                return elapsedTicks / 1000;
            }
        }
        catch
        {
        }
        return 0;
    }

    public void CancelCurrentSync()
    {
        try { _currentSyncCts?.Cancel(); } catch { }
    }

    private void RaiseStatus(string text, bool isSyncing, bool requiresLogin = false, bool hasError = false)
    {
        StatusChanged?.Invoke(this, new SyncStatusEventArgs
        {
            StatusText = text,
            IsSyncing = isSyncing,
            RequiresLogin = requiresLogin,
            HasError = hasError,
            PendingBulkDeleteCount = _pendingBulkDeleteCount,
            IsDeletePausedDueToUnverifiedAccount = _isDeletePausedDueToUnverifiedAccount,
            UnverifiedAccountReason = _unverifiedAccountReason
        });
    }

    public void Dispose()
    {
        _syncTimer.Dispose();
        _deleteDebounceTimer.Dispose();
        _watcher?.Dispose();
        _currentSyncCts?.Dispose();
    }
}


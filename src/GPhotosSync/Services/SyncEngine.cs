using GPhotosSync.Data;

namespace GPhotosSync.Services;

public class SyncStatusEventArgs : EventArgs
{
    public string StatusText { get; set; } = string.Empty;
    public bool IsSyncing { get; set; }
    public bool RequiresLogin { get; set; }
}

public class SyncEngine : IDisposable
{
    private readonly AppConfig _config;
    private readonly SyncDatabase _db;
    private readonly GooglePhotosEngine _photosEngine;
    private readonly System.Windows.Forms.Timer _syncTimer;
    private readonly System.Windows.Forms.Timer _deleteDebounceTimer;
    private FileSystemWatcher? _watcher;

    private bool _isSyncing;
    private CancellationTokenSource? _currentSyncCts;

    public event EventHandler<SyncStatusEventArgs>? StatusChanged;
    public event Action<string, string>? ShowNotification;

    public GooglePhotosEngine PhotosEngine => _photosEngine;
    public SyncDatabase Database => _db;
    public bool IsSyncing => _isSyncing;

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

        SetupFileSystemWatcher();
        UpdateTimerInterval();
    }

    public void UpdateConfiguration()
    {
        SetupFileSystemWatcher();
        UpdateTimerInterval();
    }

    private void UpdateTimerInterval()
    {
        _syncTimer.Stop();
        int minutes = Math.Clamp(_config.SyncIntervalMinutes, 1, 1440);
        _syncTimer.Interval = minutes * 60 * 1000;
        if (_config.IsFirstRunCompleted)
        {
            _syncTimer.Start();
        }
    }

    private void SetupFileSystemWatcher()
    {
        try
        {
            _watcher?.Dispose();
            _watcher = null;

            if (string.IsNullOrWhiteSpace(_config.DownloadFolder))
                return;

            Directory.CreateDirectory(_config.DownloadFolder);

            _watcher = new FileSystemWatcher(_config.DownloadFolder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
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

    private void OnLocalFileRenamed(object sender, RenamedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.OldName) || string.IsNullOrEmpty(e.Name))
            return;

        if (e.OldName.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
            e.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            return;

        _db.UpdateLocalFileName(e.OldName, e.Name);
    }

    private void OnLocalFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Name) || e.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            return;

        if (!_config.TwoWayDelete)
            return;

        var record = _db.GetByLocalFileName(e.Name);
        if (record != null)
        {
            Logger.Info($"Zjištěno odstranění/přesunutí souboru '{e.Name}' ze složky PC. Čekám na potvrzení pro přesun do koše Google Photos...");
            // Restart debounce timer on UI thread
            if (_photosEngine != null)
            {
                _deleteDebounceTimer.Stop();
                _deleteDebounceTimer.Start();
            }
        }
    }

    public async Task ProcessLocalDeletionsAsync(CancellationToken ct = default)
    {
        if (!_config.TwoWayDelete || _isSyncing)
            return;

        try
        {
            DetectMissingLocalFilesForTwoWayDelete();
            var pendingDeletes = _db.GetItemsByStatus(SyncItemStatus.PendingRemoteDelete);
            if (pendingDeletes.Count == 0)
                return;

            _isSyncing = true;
            RaiseStatus($"Přesouvám {pendingDeletes.Count} smazaných fotek do koše Google Photos...", isSyncing: true);

            int trashedCount = 0;
            foreach (var item in pendingDeletes)
            {
                ct.ThrowIfCancellationRequested();

                // Double-check the file didn't reappear (e.g. photo editor overwrite)
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
            }

            if (trashedCount > 0 && _config.ShowNotifications)
            {
                ShowNotification?.Invoke(
                    "Obousměrná synchronizace Google Photos",
                    $"Do koše v Google Photos bylo přesunuto {trashedCount} položek (smazaných/přesunutých na PC).");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Chyba při obousměrném mazání: {ex.Message}");
        }
        finally
        {
            _isSyncing = false;
            RaiseStatus("Synchronizováno – čekám na další interval", isSyncing: false);
        }
    }

    private void DetectMissingLocalFilesForTwoWayDelete()
    {
        if (!_config.TwoWayDelete || string.IsNullOrWhiteSpace(_config.DownloadFolder))
            return;

        if (!Directory.Exists(_config.DownloadFolder))
            return;

        var downloadedItems = _db.GetItemsByStatus(SyncItemStatus.Downloaded);
        foreach (var item in downloadedItems)
        {
            if (string.IsNullOrWhiteSpace(item.LocalFileName))
                continue;

            var fullPath = Path.Combine(_config.DownloadFolder, item.LocalFileName);
            if (!File.Exists(fullPath))
            {
                // Ignore if downloaded less than 10 seconds ago
                if ((DateTime.Now - item.UpdatedAt).TotalSeconds < 10)
                    continue;

                Logger.Info($"Soubor '{item.LocalFileName}' již není v synchronizované složce -> zařazuji k přesunu do koše Google Photos.");
                _db.UpdateStatus(item.MediaKey, SyncItemStatus.PendingRemoteDelete);
            }
        }
    }

    public async Task RunSyncCycleAsync(bool manualTrigger)
    {
        if (_isSyncing)
        {
            if (manualTrigger)
            {
                Logger.Info("Synchronizace již právě probíhá.");
            }
            return;
        }

        _isSyncing = true;
        _currentSyncCts = new CancellationTokenSource();
        var ct = _currentSyncCts.Token;

        try
        {
            RaiseStatus("Kontroluji přihlášení ke Google Photos...", isSyncing: true);
            bool loggedIn = await _photosEngine.CheckLoggedInAsync();
            if (!loggedIn)
            {
                Logger.Warn("Uživatel není přihlášen ke Google Photos.");
                RaiseStatus("Vyžadováno přihlášení k účtu Google", isSyncing: false, requiresLogin: true);
                if (manualTrigger)
                {
                    ShowNotification?.Invoke(
                        "Vyžadováno přihlášení",
                        "Klikněte na ikonu aplikace u hodin a přihlaste se ke Google Photos.");
                }
                return;
            }

            // 1. First process any two-way deletions (files removed or moved out of local folder)
            if (_config.TwoWayDelete)
            {
                DetectMissingLocalFilesForTwoWayDelete();
                var pendingDeletes = _db.GetItemsByStatus(SyncItemStatus.PendingRemoteDelete);
                if (pendingDeletes.Count > 0)
                {
                    RaiseStatus($"Mažu {pendingDeletes.Count} odebraných fotek z Google Photos...", isSyncing: true);
                    int deletedNow = 0;
                    foreach (var delItem in pendingDeletes)
                    {
                        ct.ThrowIfCancellationRequested();
                        var localPath = Path.Combine(_config.DownloadFolder, delItem.LocalFileName);
                        if (File.Exists(localPath))
                        {
                            _db.UpdateStatus(delItem.MediaKey, SyncItemStatus.Downloaded);
                            continue;
                        }

                        if (await _photosEngine.MoveToTrashAsync(delItem.MediaKey, delItem.PhotoUrl, ct))
                        {
                            _db.UpdateStatus(delItem.MediaKey, SyncItemStatus.DeletedLocalAndRemote);
                            deletedNow++;
                        }
                    }

                    if (deletedNow > 0)
                    {
                        Logger.Info($"Do koše v Google Photos bylo přesunuto {deletedNow} položek.");
                    }
                }
            }

            // 2. Scan Google Photos timeline for new items
            RaiseStatus("Hledám nové fotky a videa v Google Photos...", isSyncing: true);
            int scrollSteps = _config.InitialMode switch
            {
                InitialSyncMode.AllHistory => 25,
                InitialSyncMode.LastDays when _config.InitialSyncDays > 14 => 12,
                InitialSyncMode.FromDate => 15,
                _ => 5
            };

            var discovered = await _photosEngine.ScanTimelineAsync(scrollSteps, ct);
            Logger.Info($"Nalezeno {discovered.Count} položek na časové ose Google Photos.");

            // Handle "OnlyNewFromNow" first-run baseline capture so existing old photos aren't downloaded
            if (_config.InitialMode == InitialSyncMode.OnlyNewFromNow && !_config.BaselineCaptured)
            {
                foreach (var item in discovered)
                {
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
                            Status = SyncItemStatus.SkippedInitial
                        });
                    }
                }

                _config.BaselineCaptured = true;
                _config.Save();
                Logger.Info($"Výchozí stav zaznamenán ({discovered.Count} současných fotek přeskočeno). Od této chvíle se budou stahovat všechny nově vyfocené fotky a videa.");
                RaiseStatus("Připraveno – čekám na nové fotky z telefonu", isSyncing: false);
                return;
            }

            DateTime? cutoffDate = _config.InitialMode == InitialSyncMode.OnlyNewFromNow
                ? null // Already handled by baseline capture!
                : _config.GetCutoffDate();

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
                        Status = SyncItemStatus.SkippedInitial
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
                        Status = SyncItemStatus.SkippedInitial
                    });
                    continue;
                }

                newItems.Add(item);
            }

            if (newItems.Count == 0)
            {
                Logger.Info("Žádné nové položky ke stažení v tomto cyklu.");
                RaiseStatus($"Synchronizováno ({DateTime.Now:HH:mm})", isSyncing: false);
                return;
            }

            Logger.Info($"Zahajuji stahování {newItems.Count} nových položek do '{_config.DownloadFolder}'...");
            int downloadedCount = 0;

            for (int i = 0; i < newItems.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = newItems[i];

                // Re-read cutoffDate dynamically in case user changed settings while a batch is downloading
                cutoffDate = _config.InitialMode == InitialSyncMode.OnlyNewFromNow
                    ? null
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
                        Status = SyncItemStatus.SkippedInitial
                    });
                    continue;
                }

                RaiseStatus($"Stahuji ({i + 1}/{newItems.Count})...", isSyncing: true);

                var res = await _photosEngine.DownloadMediaAsync(
                    item,
                    _config.DownloadFolder,
                    _config.DownloadVideos,
                    ct);

                if (res.SkippedVideo)
                {
                    _db.UpsertItem(new SyncItemRecord
                    {
                        MediaKey = item.MediaKey,
                        PhotoUrl = item.PhotoUrl,
                        LocalFileName = res.LocalFileName,
                        TakenAt = item.EstimatedDate,
                        UpdatedAt = DateTime.Now,
                        Status = SyncItemStatus.SkippedInitial
                    });
                    continue;
                }

                if (res.Success)
                {
                    // Check if actual EXIF date is older than cutoffDate ONLY when timeline didn't already confirm it's recent
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
                            Status = SyncItemStatus.SkippedInitial
                        });
                        continue;
                    }

                    _db.UpsertItem(new SyncItemRecord
                    {
                        MediaKey = item.MediaKey,
                        PhotoUrl = item.PhotoUrl,
                        LocalFileName = res.LocalFileName,
                        FileSize = res.FileSize,
                        TakenAt = res.TakenAt ?? item.EstimatedDate,
                        UpdatedAt = DateTime.Now,
                        Status = SyncItemStatus.Downloaded
                    });

                    downloadedCount++;
                    Logger.Info($"Staženo: {res.LocalFileName} ({res.FileSize / 1024:N0} KB)");
                }
                else
                {
                    Logger.Warn($"Nepodařilo se stáhnout položku '{item.MediaKey}': {res.ErrorMessage}");
                }
            }

            RaiseStatus($"Synchronizováno ({DateTime.Now:HH:mm})", isSyncing: false);

            if (downloadedCount > 0 && _config.ShowNotifications)
            {
                ShowNotification?.Invoke(
                    "Staženy nové fotky z Google Photos",
                    $"Do složky '{Path.GetFileName(_config.DownloadFolder)}' bylo staženo {downloadedCount} nových položek.");
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
            RaiseStatus("Chyba při synchronizaci (podrobnosti v protokolu)", isSyncing: false);
        }
        finally
        {
            _isSyncing = false;
        }
    }

    public void CancelCurrentSync()
    {
        try { _currentSyncCts?.Cancel(); } catch { }
    }

    private void RaiseStatus(string text, bool isSyncing, bool requiresLogin = false)
    {
        StatusChanged?.Invoke(this, new SyncStatusEventArgs
        {
            StatusText = text,
            IsSyncing = isSyncing,
            RequiresLogin = requiresLogin
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

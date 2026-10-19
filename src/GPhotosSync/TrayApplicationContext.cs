using System.Diagnostics;
using GPhotosSync.Data;
using GPhotosSync.Forms;
using GPhotosSync.Services;

namespace GPhotosSync;

public class TrayApplicationContext : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly SyncDatabase _db;
    private readonly GooglePhotosEngine _photosEngine;
    private readonly SyncEngine _syncEngine;
    private readonly UpdateService _updateService;

    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusMenuItem;
    private readonly ToolStripMenuItem _unverifiedAccountMenuItem;
    private readonly ToolStripMenuItem _approveBulkDeleteMenuItem;
    private readonly ToolStripMenuItem _cancelBulkDeleteMenuItem;
    private readonly ToolStripMenuItem _syncNowMenuItem;
    private readonly ToolStripMenuItem _twoWayToggleMenuItem;
    private readonly ToolStripMenuItem _updateMenuItem;
    private readonly System.Windows.Forms.Timer _updateCheckTimer;

    private readonly Icon _iconOk;        // zelená: vše v pořádku
    private readonly Icon _iconAttention; // modrá: čeká se na rozhodnutí uživatele
    private readonly Icon _iconError;     // červená: chyba

    private SettingsForm? _openSettingsForm;

    public TrayApplicationContext()
    {
        _config = AppConfig.Load();
        Logger.VerboseEnabled = _config.VerboseLogging;
        _db = new SyncDatabase();
        _photosEngine = new GooglePhotosEngine();
        _syncEngine = new SyncEngine(_config, _db, _photosEngine);
        _updateService = new UpdateService(_config);

        _iconOk = IconGenerator.CreateStatusIcon(IconGenerator.Green);
        _iconAttention = IconGenerator.CreateStatusIcon(IconGenerator.Blue);
        _iconError = IconGenerator.CreateStatusIcon(IconGenerator.Red);

        _statusMenuItem = new ToolStripMenuItem("Stav: Spouštím...") { Enabled = false };

        _unverifiedAccountMenuItem = new ToolStripMenuItem("⚠️ Mazání vypnuto: Potvrdit e-mail účtu Google...")
        {
            Visible = _syncEngine.IsDeletePausedDueToUnverifiedAccount,
            ForeColor = Color.FromArgb(195, 90, 15)
        };
        _unverifiedAccountMenuItem.Click += (_, _) =>
        {
            if (SettingsForm.PromptManualGoogleAccountConfirmation(null, _config, _syncEngine, _photosEngine))
            {
                _unverifiedAccountMenuItem.Visible = _syncEngine.IsDeletePausedDueToUnverifiedAccount;
            }
        };

        bool hasInitialBlockedBatch = _syncEngine.PendingBulkDeleteCount > 0;
        _approveBulkDeleteMenuItem = new ToolStripMenuItem(
            hasInitialBlockedBatch
                ? $"⚠️ Potvrdit smazání {_syncEngine.PendingBulkDeleteCount} položek v Google Photos"
                : "⚠️ Potvrdit hromadné smazání v Google Photos",
            null,
            async (_, _) =>
            {
                await _syncEngine.ApprovePendingBulkDeleteAsync();
            })
        {
            Visible = hasInitialBlockedBatch,
            ForeColor = Color.FromArgb(190, 40, 40)
        };

        _cancelBulkDeleteMenuItem = new ToolStripMenuItem("🛡️ Zrušit hromadné mazání (nechat v Google Photos a stáhnout zpět)", null, (_, _) =>
        {
            _syncEngine.CancelPendingBulkDelete();
        })
        {
            Visible = hasInitialBlockedBatch
        };

        _syncNowMenuItem = new ToolStripMenuItem("🔄 Stáhnout nové fotky hned", null, async (_, _) =>
        {
            await _syncEngine.RunSyncCycleAsync(manualTrigger: true);
        });

        var openFolderItem = new ToolStripMenuItem("📂 Otevřít složku s fotkami", null, (_, _) => OpenDownloadFolder());

        _twoWayToggleMenuItem = new ToolStripMenuItem("🗑️ Obousměrné mazání s Google Photos")
        {
            Checked = _config.TwoWayDelete,
            CheckOnClick = true
        };
        _twoWayToggleMenuItem.Click += (_, _) =>
        {
            _config.TwoWayDelete = _twoWayToggleMenuItem.Checked;
            _config.Save();
            if (!_config.TwoWayDelete)
            {
                // Rozběhnutá dávka mazání kontroluje nastavení před každou položkou; zrušit i probíhající cyklus
                _syncEngine.CancelCurrentSync();
            }
            _syncEngine.UpdateConfiguration();
            _unverifiedAccountMenuItem.Visible = _syncEngine.IsDeletePausedDueToUnverifiedAccount;
            Logger.Info($"Obousměrné mazání bylo {(_config.TwoWayDelete ? "ZAPNUTO" : "VYPNUTO")}.");
        };

        var loginItem = new ToolStripMenuItem("🔑 Účet Google Photos (Přihlášení)...", null, async (_, _) =>
        {
            // Přihlašovací okno sdílí profil prohlížeče se synchronizací -> nejdřív ji zastavit
            await _syncEngine.StopAndWaitForIdleAsync();
            try
            {
            using var loginForm = new GoogleLoginForm(_photosEngine);
            loginForm.ShowDialog();
            if (loginForm.LoginSucceeded)
            {
                _config.ExpectedGoogleAccountEmail = string.Empty;
                var check = await _photosEngine.CheckLoginAndAccountAsync();
                if (!string.IsNullOrWhiteSpace(check.DetectedEmail))
                {
                    _syncEngine.ConfirmGoogleAccountEmailManually(check.DetectedEmail);
                }
                else
                {
                    SettingsForm.PromptManualGoogleAccountConfirmation(null, _config, _syncEngine, _photosEngine);
                }
            }
            }
            finally
            {
                _syncEngine.UpdateConfiguration();
            }
            _unverifiedAccountMenuItem.Visible = _syncEngine.IsDeletePausedDueToUnverifiedAccount;
            await _syncEngine.RunSyncCycleAsync(manualTrigger: true);
        });

        var settingsItem = new ToolStripMenuItem("⚙️ Nastavení a protokol...", null, (_, _) => ShowSettingsWindow());

        _updateMenuItem = new ToolStripMenuItem("⬆️ Zkontrolovat aktualizace (Gitea)...", null, async (_, _) =>
        {
            await CheckAndPromptUpdateAsync(silentIfUpToDate: false);
        });

        var exitItem = new ToolStripMenuItem("❌ Ukončit", null, (_, _) => ExitApplication());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusMenuItem);
        menu.Items.Add(_unverifiedAccountMenuItem);
        menu.Items.Add(_approveBulkDeleteMenuItem);
        menu.Items.Add(_cancelBulkDeleteMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_syncNowMenuItem);
        menu.Items.Add(openFolderItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_twoWayToggleMenuItem);
        menu.Items.Add(loginItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(_updateMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = hasInitialBlockedBatch ? _iconAttention : _iconOk,
            Text = "Google Photos Sync – Běží na pozadí",
            ContextMenuStrip = menu,
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => OpenDownloadFolder();
        _trayIcon.BalloonTipClicked += (_, _) =>
        {
            if (_syncEngine.PendingBulkDeleteCount > 0 || _syncEngine.IsDeletePausedDueToUnverifiedAccount)
            {
                _trayIcon.ContextMenuStrip?.Show(Cursor.Position);
            }
            else
            {
                OpenDownloadFolder();
            }
        };

        _syncEngine.StatusChanged += OnSyncStatusChanged;
        _syncEngine.ShowNotification += (title, msg, icon) =>
        {
            _trayIcon.ShowBalloonTip(5000, title, msg, icon);
        };

        // Check for Gitea updates every 6 hours
        _updateCheckTimer = new System.Windows.Forms.Timer { Interval = 6 * 60 * 60 * 1000 };
        _updateCheckTimer.Tick += async (_, _) =>
        {
            if (_config.AutoCheckUpdates && _updateService.IsConfigured)
            {
                await CheckAndPromptUpdateAsync(silentIfUpToDate: true);
            }
        };
        _updateCheckTimer.Start();

        // Ensure Windows startup matches config
        StartupManager.SetStartWithWindows(_config.StartWithWindows);

        // Kick off initial startup flow asynchronously on UI thread
        var initTimer = new System.Windows.Forms.Timer { Interval = 400 };
        initTimer.Tick += async (_, _) =>
        {
            initTimer.Stop();
            initTimer.Dispose();
            await StartupFlowAsync();
        };
        initTimer.Start();
    }

    private async Task StartupFlowAsync()
    {
        Logger.Info("Aplikace Google Photos Sync byla spuštěna.");

        // Bod 3 & 12: Pokud config.json chybí nebo je poškozený při existující DB, nabídnout obnovu z config.json.bak nebo otevřít Nastavení
        if (_config.HasConfigLoadError)
        {
            if (AppConfig.HasBackupFile())
            {
                var restoreAsk = MessageBox.Show(
                    $"{_config.ConfigLoadErrorMessage}\r\n\r\n" +
                    "Byl nalezen záložní konfigurační soubor (config.json.bak).\r\n" +
                    "Chcete konfiguraci nyní automaticky obnovit ze zálohy?",
                    "Obnova konfiguračního souboru Google Photos Sync",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                string? restoreErr = null;
                if (restoreAsk == DialogResult.Yes && _config.TryRestoreFromBackup(out restoreErr))
                {
                    _twoWayToggleMenuItem.Checked = _config.TwoWayDelete;
                    _syncEngine.UpdateConfiguration();
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(restoreErr))
                    {
                        MessageBox.Show(restoreErr, "Chyba obnovy ze zálohy", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    ShowSettingsWindow();
                    return;
                }
            }
            else
            {
                MessageBox.Show(
                    $"{_config.ConfigLoadErrorMessage}\r\n\r\n" +
                    "Zkontrolujte a uložte prosím cílovou složku v okně Nastavení.",
                    "Chybějící nebo poškozená konfigurace",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                ShowSettingsWindow();
                return;
            }
        }

        if (!_config.IsFirstRunCompleted && !_config.HasConfigLoadError)
        {
            using var wizard = new SetupWizardForm(_config, _photosEngine, _db);
            var res = wizard.ShowDialog();
            _twoWayToggleMenuItem.Checked = _config.TwoWayDelete;
            _syncEngine.UpdateConfiguration();
            _unverifiedAccountMenuItem.Visible = _syncEngine.IsDeletePausedDueToUnverifiedAccount;

            if (res == DialogResult.OK)
            {
                _trayIcon.ShowBalloonTip(
                    4000,
                    "Google Photos Sync běží na pozadí",
                    $"Fotky se budou automaticky ukládat do: {_config.DownloadFolder}",
                    ToolTipIcon.Info);
            }
        }

        if (_config.AutoCheckUpdates && _updateService.IsConfigured)
        {
            _ = CheckAndPromptUpdateAsync(silentIfUpToDate: true);
        }

        await _syncEngine.RunSyncCycleAsync(manualTrigger: false);
    }

    private async Task CheckAndPromptUpdateAsync(bool silentIfUpToDate)
    {
        if (!_updateService.IsConfigured)
        {
            if (!silentIfUpToDate)
            {
                ShowSettingsWindow();
            }
            return;
        }

        var res = await _updateService.CheckForUpdatesAsync();
        if (!string.IsNullOrWhiteSpace(res.ErrorMessage))
        {
            if (!silentIfUpToDate)
            {
                MessageBox.Show(res.ErrorMessage, "Kontrola aktualizací Gitea", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return;
        }

        if (!res.IsUpdateAvailable)
        {
            if (!silentIfUpToDate)
            {
                MessageBox.Show(
                    $"Používáte nejnovější verzi ({res.CurrentVersion}).",
                    "Žádná nová aktualizace",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return;
        }

        _updateMenuItem.Text = $"⬆️ Nainstalovat novou verzi {res.LatestVersion}!";
        Logger.Info($"Na Gitea serveru je dostupná nová verze {res.LatestVersion} (současná: {res.CurrentVersion}).");

        if (_config.AutoInstallUpdates && !_syncEngine.IsSyncing)
        {
            Logger.Info("Automatická tichá aktualizace je povolena -> stahuji a instaluji novou verzi z Gitea...");
            await _updateService.DownloadAndApplyUpdateAsync(res.DownloadUrl, ExitApplication);
            return;
        }

        var ask = MessageBox.Show(
            $"Na Gitea serveru byla nalezena nová verze Google Photos Sync ({res.LatestVersion}).\r\n\r\n" +
            $"Změny:\r\n{res.ReleaseNotes}\r\n\r\nChcete aplikaci nyní automaticky aktualizovat?",
            "Dostupná nová verze",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);

        if (ask == DialogResult.Yes)
        {
            await _updateService.DownloadAndApplyUpdateAsync(res.DownloadUrl, ExitApplication);
        }
    }

    private void OnSyncStatusChanged(object? sender, SyncStatusEventArgs e)
    {
        _statusMenuItem.Text = $"Stav: {e.StatusText}";
        var shortTooltip = $"GPhotos Sync: {e.StatusText}";
        if (shortTooltip.Length > 63) shortTooltip = shortTooltip[..63];
        _trayIcon.Text = shortTooltip;

        _unverifiedAccountMenuItem.Visible = e.IsDeletePausedDueToUnverifiedAccount;

        bool hasPendingBulkDelete = e.PendingBulkDeleteCount > 0;
        _approveBulkDeleteMenuItem.Visible = hasPendingBulkDelete;
        _cancelBulkDeleteMenuItem.Visible = hasPendingBulkDelete;
        if (hasPendingBulkDelete)
        {
            _approveBulkDeleteMenuItem.Text = $"⚠️ Potvrdit smazání {e.PendingBulkDeleteCount} položek v Google Photos";
        }

        // Modrá = čeká se na uživatele (má přednost, i když stav nese příznak chyby), červená = chyba, jinak zelená
        if (hasPendingBulkDelete || e.RequiresLogin || e.IsDeletePausedDueToUnverifiedAccount)
        {
            _trayIcon.Icon = _iconAttention;
        }
        else if (e.HasError)
        {
            _trayIcon.Icon = _iconError;
        }
        else
        {
            _trayIcon.Icon = _iconOk;
        }
    }

    private void OpenDownloadFolder()
    {
        try
        {
            if (_config.HasConfigLoadError)
            {
                ShowSettingsWindow();
                return;
            }

            if (!Directory.Exists(_config.DownloadFolder))
            {
                if (_db.HasDownloadedOrPendingDeleteItems())
                {
                    MessageBox.Show(
                        $"Cílová složka '{_config.DownloadFolder}' nebyla nalezena.\r\n" +
                        "Zkontrolujte prosím připojení disku nebo upravte umístění v Nastavení.",
                        "Složka nenalezena",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                _config.InitializeFolderMarker(_config.DownloadFolder);
                _config.Save();
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_config.DownloadFolder}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Error($"Nelze otevřít složku: {ex.Message}");
        }
    }

    private void ShowSettingsWindow()
    {
        if (_openSettingsForm != null && !_openSettingsForm.IsDisposed)
        {
            _openSettingsForm.Activate();
            return;
        }

        _openSettingsForm = new SettingsForm(_config, _syncEngine, _updateService, ExitApplication);
        _openSettingsForm.FormClosed += (_, _) =>
        {
            _twoWayToggleMenuItem.Checked = _config.TwoWayDelete;
            _unverifiedAccountMenuItem.Visible = _syncEngine.IsDeletePausedDueToUnverifiedAccount;
            _openSettingsForm = null;
        };
        _openSettingsForm.Show();
    }

    private void ExitApplication()
    {
        _updateCheckTimer.Stop();
        _updateCheckTimer.Dispose();
        _syncEngine.CancelCurrentSync();
        _trayIcon.Visible = false;
        _syncEngine.Dispose();
        _photosEngine.Dispose();
        _trayIcon.Dispose();
        Application.Exit();
    }
}

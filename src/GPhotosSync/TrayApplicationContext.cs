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
    private readonly ToolStripMenuItem _syncNowMenuItem;
    private readonly ToolStripMenuItem _twoWayToggleMenuItem;
    private readonly ToolStripMenuItem _updateMenuItem;
    private readonly System.Windows.Forms.Timer _updateCheckTimer;

    private readonly Icon _iconIdle;
    private readonly Icon _iconSyncing;
    private readonly Icon _iconWarn;

    private SettingsForm? _openSettingsForm;

    public TrayApplicationContext()
    {
        _config = AppConfig.Load();
        _db = new SyncDatabase();
        _photosEngine = new GooglePhotosEngine();
        _syncEngine = new SyncEngine(_config, _db, _photosEngine);
        _updateService = new UpdateService(_config);

        _iconIdle = IconGenerator.CreateTrayIcon(Color.FromArgb(40, 185, 85));    // Green badge
        _iconSyncing = IconGenerator.CreateTrayIcon(Color.FromArgb(30, 140, 255)); // Blue badge
        _iconWarn = IconGenerator.CreateTrayIcon(Color.FromArgb(235, 115, 25));    // Orange badge

        _statusMenuItem = new ToolStripMenuItem("Stav: Spouštím...") { Enabled = false };
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
            Logger.Info($"Obousměrné mazání bylo {(_config.TwoWayDelete ? "ZAPNUTO" : "VYPNUTO")}.");
        };

        var loginItem = new ToolStripMenuItem("🔑 Účet Google Photos (Přihlášení)...", null, async (_, _) =>
        {
            using var loginForm = new GoogleLoginForm(_photosEngine);
            loginForm.ShowDialog();
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
            Icon = _iconIdle,
            Text = "Google Photos Sync – Běží na pozadí",
            ContextMenuStrip = menu,
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => OpenDownloadFolder();
        _trayIcon.BalloonTipClicked += (_, _) => OpenDownloadFolder();

        _syncEngine.StatusChanged += OnSyncStatusChanged;
        _syncEngine.ShowNotification += (title, msg) =>
        {
            _trayIcon.ShowBalloonTip(4000, title, msg, ToolTipIcon.Info);
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

        if (!_config.IsFirstRunCompleted)
        {
            using var wizard = new SetupWizardForm(_config, _photosEngine);
            var res = wizard.ShowDialog();
            _twoWayToggleMenuItem.Checked = _config.TwoWayDelete;
            _syncEngine.UpdateConfiguration();

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

        if (e.RequiresLogin)
        {
            _trayIcon.Icon = _iconWarn;
        }
        else if (e.IsSyncing)
        {
            _trayIcon.Icon = _iconSyncing;
        }
        else
        {
            _trayIcon.Icon = _iconIdle;
        }
    }

    private void OpenDownloadFolder()
    {
        try
        {
            Directory.CreateDirectory(_config.DownloadFolder);
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

using System.Diagnostics;
using GPhotosSync.Data;
using GPhotosSync.Services;

namespace GPhotosSync.Forms;

public class SettingsForm : Form
{
    private readonly AppConfig _config;
    private readonly SyncEngine _syncEngine;
    private readonly UpdateService _updateService;
    private readonly Action _exitCallback;

    private readonly TextBox _txtFolder;
    private readonly NumericUpDown _numInterval;
    private readonly CheckBox _chkVideos;
    private readonly CheckBox _chkTwoWayDelete;
    private readonly CheckBox _chkStartWithWindows;
    private readonly CheckBox _chkNotifications;

    // Gitea Auto-Update controls
    private readonly TextBox _txtGiteaUrl;
    private readonly TextBox _txtGiteaOwner;
    private readonly TextBox _txtGiteaRepo;
    private readonly TextBox _txtGiteaToken;
    private readonly CheckBox _chkAutoCheckUpdates;
    private readonly CheckBox _chkAutoInstallUpdates;

    private readonly Label _lblStats;
    private readonly TextBox _txtLogs;

    public SettingsForm(AppConfig config, SyncEngine syncEngine, UpdateService updateService, Action exitCallback)
    {
        _config = config;
        _syncEngine = syncEngine;
        _updateService = updateService;
        _exitCallback = exitCallback;

        var ver = UpdateService.GetCurrentVersion();
        Text = $"Nastavení a stav – Google Photos Sync (v{ver.Major}.{ver.Minor}.{Math.Max(0, ver.Build)})";
        Width = 680;
        Height = 760;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.White;

        var grpFolder = new GroupBox
        {
            Text = "Složka pro synchronizaci ve Windows",
            Location = new Point(18, 12),
            Size = new Size(628, 78)
        };

        _txtFolder = new TextBox
        {
            Text = _config.DownloadFolder,
            Location = new Point(14, 30),
            Size = new Size(395, 27)
        };

        var btnBrowse = new Button
        {
            Text = "Změnit...",
            Location = new Point(416, 28),
            Size = new Size(95, 29)
        };
        btnBrowse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                SelectedPath = _txtFolder.Text,
                UseDescriptionForTitle = true,
                Description = "Vyberte cílovou složku pro fotky z Google Photos"
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _txtFolder.Text = dlg.SelectedPath;
            }
        };

        var btnOpenFolder = new Button
        {
            Text = "Otevřít složku",
            Location = new Point(517, 28),
            Size = new Size(97, 29)
        };
        btnOpenFolder.Click += (_, _) =>
        {
            if (Directory.Exists(_txtFolder.Text))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_txtFolder.Text}\"") { UseShellExecute = true });
            }
        };

        grpFolder.Controls.Add(_txtFolder);
        grpFolder.Controls.Add(btnBrowse);
        grpFolder.Controls.Add(btnOpenFolder);

        var grpSync = new GroupBox
        {
            Text = "Pravidla synchronizace a obousměrného mazání",
            Location = new Point(18, 98),
            Size = new Size(628, 178)
        };

        var lblInterval = new Label
        {
            Text = "Kontrolovat nové fotky každých:",
            Location = new Point(14, 28),
            AutoSize = true
        };

        _numInterval = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 720,
            Value = Math.Clamp(_config.SyncIntervalMinutes, 1, 720),
            Location = new Point(220, 25),
            Width = 65
        };

        var lblMinutes = new Label
        {
            Text = "minut",
            Location = new Point(292, 28),
            AutoSize = true
        };

        var btnAccount = new Button
        {
            Text = "Účet Google Photos...",
            Location = new Point(445, 23),
            Size = new Size(168, 30)
        };
        btnAccount.Click += (_, _) =>
        {
            using var loginForm = new GoogleLoginForm(_syncEngine.PhotosEngine);
            loginForm.ShowDialog(this);
        };

        _chkVideos = new CheckBox
        {
            Text = "Stahovat kromě fotek také videa (v plné kvalitě)",
            Checked = _config.DownloadVideos,
            Location = new Point(14, 62),
            AutoSize = true
        };

        _chkTwoWayDelete = new CheckBox
        {
            Text = "Obousměrné mazání: Při smazání nebo přesunutí fotky ze složky v PC ji přesunout do koše i v Google Photos",
            Checked = _config.TwoWayDelete,
            Location = new Point(14, 90),
            Size = new Size(605, 26),
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(20, 60, 130)
        };

        _chkStartWithWindows = new CheckBox
        {
            Text = "Spouštět automaticky po startu Windows na pozadí",
            Checked = _config.StartWithWindows,
            Location = new Point(14, 118),
            AutoSize = true
        };

        _chkNotifications = new CheckBox
        {
            Text = "Zobrazovat oznámení Windows při stažení nových fotek",
            Checked = _config.ShowNotifications,
            Location = new Point(14, 146),
            AutoSize = true
        };

        grpSync.Controls.Add(lblInterval);
        grpSync.Controls.Add(_numInterval);
        grpSync.Controls.Add(lblMinutes);
        grpSync.Controls.Add(btnAccount);
        grpSync.Controls.Add(_chkVideos);
        grpSync.Controls.Add(_chkTwoWayDelete);
        grpSync.Controls.Add(_chkStartWithWindows);
        grpSync.Controls.Add(_chkNotifications);

        // Gitea Auto-Update Section
        var grpGitea = new GroupBox
        {
            Text = "Automatické aktualizace z vlastního Gitea serveru (Reverse Proxy / LAN)",
            Location = new Point(18, 284),
            Size = new Size(628, 152)
        };

        var lblGiteaUrl = new Label { Text = "Gitea URL:", Location = new Point(14, 28), AutoSize = true };
        _txtGiteaUrl = new TextBox
        {
            Text = _config.GiteaServerUrl,
            PlaceholderText = "https://git.mojedomena.cz",
            Location = new Point(95, 25),
            Size = new Size(235, 26)
        };

        var lblOwner = new Label { Text = "Vlastník/Repo:", Location = new Point(340, 28), AutoSize = true };
        _txtGiteaOwner = new TextBox
        {
            Text = _config.GiteaRepoOwner,
            PlaceholderText = "uzivatel",
            Location = new Point(435, 25),
            Size = new Size(80, 26)
        };

        var lblSlash = new Label { Text = "/", Location = new Point(518, 28), AutoSize = true };
        _txtGiteaRepo = new TextBox
        {
            Text = _config.GiteaRepoName,
            PlaceholderText = "gphotos-sync",
            Location = new Point(530, 25),
            Size = new Size(84, 26)
        };

        var lblToken = new Label { Text = "API Token (jen pro privátní repo):", Location = new Point(14, 62), AutoSize = true };
        _txtGiteaToken = new TextBox
        {
            Text = _config.GiteaAccessToken,
            PlaceholderText = "nepovinné (pro veřejný repozitář nechte prázdné)",
            UseSystemPasswordChar = true,
            Location = new Point(225, 59),
            Size = new Size(220, 26)
        };

        var btnCheckUpdate = new Button
        {
            Text = "Zkontrolovat verzi...",
            Location = new Point(455, 57),
            Size = new Size(159, 29)
        };
        btnCheckUpdate.Click += async (_, _) => await OnManualUpdateCheckAsync();

        _chkAutoCheckUpdates = new CheckBox
        {
            Text = "Automaticky kontrolovat nové verze na Gitea serveru (při startu a každých 6 hodin)",
            Checked = _config.AutoCheckUpdates,
            Location = new Point(14, 94),
            AutoSize = true
        };

        _chkAutoInstallUpdates = new CheckBox
        {
            Text = "Nové verze rovnou tiše nainstalovat na pozadí (bez ptaní)",
            Checked = _config.AutoInstallUpdates,
            Location = new Point(14, 120),
            AutoSize = true
        };

        grpGitea.Controls.Add(lblGiteaUrl);
        grpGitea.Controls.Add(_txtGiteaUrl);
        grpGitea.Controls.Add(lblOwner);
        grpGitea.Controls.Add(_txtGiteaOwner);
        grpGitea.Controls.Add(lblSlash);
        grpGitea.Controls.Add(_txtGiteaRepo);
        grpGitea.Controls.Add(lblToken);
        grpGitea.Controls.Add(_txtGiteaToken);
        grpGitea.Controls.Add(btnCheckUpdate);
        grpGitea.Controls.Add(_chkAutoCheckUpdates);
        grpGitea.Controls.Add(_chkAutoInstallUpdates);

        var grpLogs = new GroupBox
        {
            Text = "Stav a protokol synchronizace",
            Location = new Point(18, 444),
            Size = new Size(628, 205)
        };

        var stats = _syncEngine.Database.GetStatistics();
        _lblStats = new Label
        {
            Text = $"Stažené fotky/videa: {stats.downloadedCount}   |   Smazáno obousměrně v Google Photos: {stats.deletedCount}",
            Location = new Point(14, 22),
            AutoSize = true,
            ForeColor = Color.FromArgb(40, 80, 140)
        };

        _txtLogs = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Location = new Point(14, 46),
            Size = new Size(600, 145),
            Font = new Font("Consolas", 8.5f),
            BackColor = Color.FromArgb(250, 250, 252),
            Text = Logger.ReadRecentLogs(120)
        };

        grpLogs.Controls.Add(_lblStats);
        grpLogs.Controls.Add(_txtLogs);

        var btnWizard = new Button
        {
            Text = "Průvodce nastavením...",
            Location = new Point(18, 662),
            Size = new Size(155, 36)
        };
        btnWizard.Click += async (_, _) =>
        {
            using var wiz = new SetupWizardForm(_config, _syncEngine.PhotosEngine);
            if (wiz.ShowDialog(this) == DialogResult.OK)
            {
                _txtFolder.Text = _config.DownloadFolder;
                _chkVideos.Checked = _config.DownloadVideos;
                _chkTwoWayDelete.Checked = _config.TwoWayDelete;
                _chkStartWithWindows.Checked = _config.StartWithWindows;
                _syncEngine.Database.ClearSkippedInitialItems();
                _syncEngine.UpdateConfiguration();
                _syncEngine.CancelCurrentSync();
                await Task.Delay(300);
                await _syncEngine.RunSyncCycleAsync(manualTrigger: true);
            }
        };

        var btnSyncNow = new Button
        {
            Text = "Synchronizovat nyní",
            Location = new Point(180, 662),
            Size = new Size(150, 36),
            BackColor = Color.FromArgb(26, 115, 232),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnSyncNow.FlatAppearance.BorderSize = 0;
        btnSyncNow.Click += async (_, _) => await _syncEngine.RunSyncCycleAsync(manualTrigger: true);

        var btnShowBrowser = new Button
        {
            Text = "🌐 Zobrazit prohlížeč",
            Location = new Point(337, 662),
            Size = new Size(142, 36)
        };
        btnShowBrowser.Click += (_, _) => _syncEngine.PhotosEngine.ShowDiagnosticBrowserWindow();

        var btnSave = new Button
        {
            Text = "Uložit a zavřít",
            Location = new Point(486, 662),
            Size = new Size(160, 36),
            BackColor = Color.FromArgb(24, 138, 66),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnSave.FlatAppearance.BorderSize = 0;
        btnSave.Click += OnSaveClicked;

        Controls.Add(grpFolder);
        Controls.Add(grpSync);
        Controls.Add(grpGitea);
        Controls.Add(grpLogs);
        Controls.Add(btnWizard);
        Controls.Add(btnSyncNow);
        Controls.Add(btnShowBrowser);
        Controls.Add(btnSave);

        Logger.OnLogAdded += OnNewLogLine;
        FormClosed += (_, _) => Logger.OnLogAdded -= OnNewLogLine;
    }

    private async Task OnManualUpdateCheckAsync()
    {
        SaveFieldsToConfig();
        var res = await _updateService.CheckForUpdatesAsync();
        if (!string.IsNullOrWhiteSpace(res.ErrorMessage))
        {
            MessageBox.Show(res.ErrorMessage, "Kontrola aktualizací Gitea", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!res.IsUpdateAvailable)
        {
            MessageBox.Show(
                $"Používáte nejnovější verzi aplikace ({res.CurrentVersion}).",
                "Aplikace je aktuální",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var ask = MessageBox.Show(
            $"Na vašem Gitea serveru je k dispozici nová verze {res.LatestVersion} (vaše verze: {res.CurrentVersion}).\r\n\r\n" +
            $" změny:\r\n{res.ReleaseNotes}\r\n\r\nChcete novou verzi nyní automaticky stáhnout a nainstalovat?",
            "Dostupná aktualizace",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);

        if (ask == DialogResult.Yes)
        {
            await _updateService.DownloadAndApplyUpdateAsync(res.DownloadUrl, _exitCallback);
        }
    }

    private void OnNewLogLine(string line)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                _txtLogs.AppendText(Environment.NewLine + line);
                var stats = _syncEngine.Database.GetStatistics();
                _lblStats.Text = $"Stažené fotky/videa: {stats.downloadedCount}   |   Smazáno obousměrně v Google Photos: {stats.deletedCount}";
            });
        }
        catch
        {
            // Ignore
        }
    }

    private void SaveFieldsToConfig()
    {
        _config.DownloadFolder = _txtFolder.Text.Trim();
        _config.SyncIntervalMinutes = (int)_numInterval.Value;
        _config.DownloadVideos = _chkVideos.Checked;
        _config.TwoWayDelete = _chkTwoWayDelete.Checked;
        _config.StartWithWindows = _chkStartWithWindows.Checked;
        _config.ShowNotifications = _chkNotifications.Checked;

        _config.GiteaServerUrl = _txtGiteaUrl.Text.Trim();
        _config.GiteaRepoOwner = _txtGiteaOwner.Text.Trim();
        _config.GiteaRepoName = _txtGiteaRepo.Text.Trim();
        _config.GiteaAccessToken = _txtGiteaToken.Text.Trim();
        _config.AutoCheckUpdates = _chkAutoCheckUpdates.Checked;
        _config.AutoInstallUpdates = _chkAutoInstallUpdates.Checked;
        _config.Save();
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        SaveFieldsToConfig();
        StartupManager.SetStartWithWindows(_config.StartWithWindows);
        _syncEngine.UpdateConfiguration();

        DialogResult = DialogResult.OK;
        Close();
    }
}

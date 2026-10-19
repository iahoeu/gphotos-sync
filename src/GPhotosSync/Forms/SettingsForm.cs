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
        btnAccount.Click += async (_, _) =>
        {
            using var loginForm = new GoogleLoginForm(_syncEngine.PhotosEngine);
            loginForm.ShowDialog(this);
            if (loginForm.LoginSucceeded)
            {
                _config.ExpectedGoogleAccountEmail = string.Empty;
                var check = await _syncEngine.PhotosEngine.CheckLoginAndAccountAsync();
                if (!string.IsNullOrWhiteSpace(check.DetectedEmail))
                {
                    _syncEngine.ConfirmGoogleAccountEmailManually(check.DetectedEmail);
                }
                else
                {
                    PromptManualGoogleAccountConfirmation(this, _config, _syncEngine, _syncEngine.PhotosEngine);
                }
            }
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

        // Bod 3 & 12: Tlačítko pro obnovu konfigurace ze zálohy config.json.bak, pokud je config poškozen nebo chybí
        if (_config.HasConfigLoadError && AppConfig.HasBackupFile())
        {
            var btnRestoreBak = new Button
            {
                Text = "Obnovit config.json.bak",
                Location = new Point(445, 16),
                Size = new Size(168, 26),
                BackColor = Color.FromArgb(230, 126, 34),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnRestoreBak.FlatAppearance.BorderSize = 0;
            btnRestoreBak.Click += async (_, _) =>
            {
                await _syncEngine.StopAndWaitForIdleAsync();
                try
                {
                if (_config.TryRestoreFromBackup(out var err))
                {
                    _txtFolder.Text = _config.DownloadFolder;
                    _numInterval.Value = Math.Clamp(_config.SyncIntervalMinutes, 1, 720);
                    _chkVideos.Checked = _config.DownloadVideos;
                    _chkTwoWayDelete.Checked = _config.TwoWayDelete;
                    _chkStartWithWindows.Checked = _config.StartWithWindows;
                    _chkNotifications.Checked = _config.ShowNotifications;
                    _txtGiteaUrl.Text = _config.GiteaServerUrl;
                    _txtGiteaOwner.Text = _config.GiteaRepoOwner;
                    _txtGiteaRepo.Text = _config.GiteaRepoName;
                    _txtGiteaToken.Text = _config.GiteaAccessToken;
                    _chkAutoCheckUpdates.Checked = _config.AutoCheckUpdates;
                    _chkAutoInstallUpdates.Checked = _config.AutoInstallUpdates;
                    _syncEngine.UpdateConfiguration();
                    btnRestoreBak.Visible = false;
                    MessageBox.Show(this, "Konfigurace byla úspěšně obnovena ze záložního souboru config.json.bak.", "Obnova dokončena", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(this, err ?? "Obnova ze zálohy se nezdařila.", "Chyba obnovy", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                }
                finally
                {
                    _syncEngine.UpdateConfiguration();
                }
            };
            grpLogs.Controls.Add(btnRestoreBak);
        }

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
            await _syncEngine.StopAndWaitForIdleAsync();
            using var wiz = new SetupWizardForm(_config, _syncEngine.PhotosEngine, _syncEngine.Database);
            if (wiz.ShowDialog(this) != DialogResult.OK)
            {
                // Zrušený průvodce: obnovit časovače zastavené před jeho otevřením
                _syncEngine.UpdateConfiguration();
            }
            else
            {
                _txtFolder.Text = _config.DownloadFolder;
                _chkVideos.Checked = _config.DownloadVideos;
                _chkTwoWayDelete.Checked = _config.TwoWayDelete;
                _chkStartWithWindows.Checked = _config.StartWithWindows;
                _syncEngine.Database.ClearSkippedInitialItems();
                if (_config.DownloadVideos)
                {
                    _syncEngine.Database.ClearSkippedVideos();
                }
                _syncEngine.UpdateConfiguration();
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

    /// <summary>
    /// Bod 8 + Doplněk: Když e-mail ze stránky Google Photos zjistit nejde, nabídne uživateli
    /// ruční potvrzení a uložení e-mailu přihlášeného účtu Google, aby mohlo fungovat mazání.
    /// </summary>
    public static bool PromptManualGoogleAccountConfirmation(
        IWin32Window? owner,
        AppConfig config,
        SyncEngine? syncEngine,
        GooglePhotosEngine photosEngine)
    {
        using var dlg = new Form
        {
            Text = "Potvrzení účtu Google Photos pro obousměrné mazání",
            Width = 500,
            Height = 250,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
            Font = new Font("Segoe UI", 9.5f),
            BackColor = Color.White
        };

        var lblInfo = new Label
        {
            Text = "Přihlášení ke Google Photos proběhlo, ale ze stránky se nepodařilo automaticky přečíst e-mail účtu.\r\n\r\n" +
                   "Pro povolení obousměrného mazání zadejte a potvrďte e-mailovou adresu účtu Google, ke kterému jste přihlášeni (pokud nevyplníte, stahování poběží, ale mazání zůstane vypnuté):",
            Location = new Point(18, 16),
            Size = new Size(450, 90)
        };

        var txtEmail = new TextBox
        {
            Text = config.ExpectedGoogleAccountEmail ?? string.Empty,
            PlaceholderText = "vas.ucet@gmail.com",
            Location = new Point(18, 112),
            Size = new Size(448, 28)
        };

        var btnConfirm = new Button
        {
            Text = "Potvrdit a uložit účet",
            Location = new Point(180, 156),
            Size = new Size(165, 34),
            BackColor = Color.FromArgb(24, 138, 66),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnConfirm.FlatAppearance.BorderSize = 0;

        var btnSkip = new Button
        {
            Text = "Ponechat mazání vypnuté",
            Location = new Point(352, 156),
            Size = new Size(114, 34),
            DialogResult = DialogResult.Cancel
        };

        btnConfirm.Click += (_, _) =>
        {
            var email = txtEmail.Text.Trim();
            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') || !email.Contains('.'))
            {
                MessageBox.Show(dlg, "Zadejte prosím platnou e-mailovou adresu účtu Google.", "Neplatný e-mail", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (syncEngine != null)
            {
                syncEngine.ConfirmGoogleAccountEmailManually(email);
            }
            else
            {
                config.ExpectedGoogleAccountEmail = email;
                photosEngine.ConfirmCurrentSessionAccountEmail(email);
                config.Save();
            }

            dlg.DialogResult = DialogResult.OK;
            dlg.Close();
        };

        dlg.Controls.Add(lblInfo);
        dlg.Controls.Add(txtEmail);
        dlg.Controls.Add(btnConfirm);
        dlg.Controls.Add(btnSkip);
        dlg.AcceptButton = btnConfirm;
        dlg.CancelButton = btnSkip;

        return dlg.ShowDialog(owner) == DialogResult.OK;
    }

    private async Task OnManualUpdateCheckAsync()
    {
        if (!await SaveFieldsToConfigAsync())
            return;

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
            $"Změny:\r\n{res.ReleaseNotes}\r\n\r\nChcete novou verzi nyní automaticky stáhnout a nainstalovat?",
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

    /// <summary>
    /// P0-1: Bezpečné přepnutí cílové složky. Nikdy nevyvolá smazání dříve stažených fotek v Google Photos.
    /// Uživatel si zvolí buď přesun souborů do nové složky, nebo přepojení evidence na novou složku.
    /// </summary>
    public static bool ApplyFolderChangeSafely(IWin32Window? owner, AppConfig config, SyncDatabase? db, string newFolderRaw)
    {
        var newFolder = (newFolderRaw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(newFolder))
        {
            MessageBox.Show(owner, "Vyberte prosím platnou cílovou složku.", "Chybí složka", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var oldFolder = (config.DownloadFolder ?? "").Trim();
        bool isSameFolder = false;
        if (!string.IsNullOrWhiteSpace(oldFolder))
        {
            try
            {
                isSameFolder = string.Equals(
                    Path.GetFullPath(oldFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(newFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                isSameFolder = string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (isSameFolder && !config.HasConfigLoadError)
        {
            if (string.IsNullOrWhiteSpace(config.FolderId))
            {
                config.InitializeFolderMarker(newFolder);
            }
            config.DownloadFolder = newFolder;
            return true;
        }

        var downloadedItems = db?.GetItemsByStatus(SyncItemStatus.Downloaded) ?? [];
        var pendingDeletes = db?.GetItemsByStatus(SyncItemStatus.PendingRemoteDelete) ?? [];
        int totalTracked = downloadedItems.Count + pendingDeletes.Count;

        // Pokud jde o stejnou složku při obnově configu, ověříme nebo připíšeme marker, pokud ve složce soubory existují
        if (isSameFolder && config.HasConfigLoadError)
        {
            if (!Directory.Exists(newFolder) && totalTracked > 0)
            {
                MessageBox.Show(owner,
                    $"Složka '{newFolder}' neexistuje, přestože evidence obsahuje {totalTracked} stažených položek.\r\n" +
                    "Zkontrolujte připojení disku nebo zvolte složku, ve které jsou stažené fotky.",
                    "Složka nenalezena", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            Directory.CreateDirectory(newFolder);
            config.DownloadFolder = newFolder;
            config.InitializeFolderMarker(newFolder);

            // Složku přijmout jen tehdy, když prokazatelně patří k evidenci (shodná identita uložená v DB,
            // nebo – pokud identita v DB ještě není – jsou v ní evidované soubory).
            var storedFolderId = db?.GetState(SyncEngine.StateKeyFolderId);
            bool anyTrackedOnDisk = downloadedItems.Any(i =>
                !string.IsNullOrWhiteSpace(i.LocalFileName) && File.Exists(Path.Combine(newFolder, i.LocalFileName)));
            bool belongsToEvidence = totalTracked == 0 ||
                (string.IsNullOrWhiteSpace(storedFolderId)
                    ? anyTrackedOnDisk
                    : string.Equals(storedFolderId, config.FolderId, StringComparison.Ordinal));

            if (!belongsToEvidence && db != null)
            {
                var unlinkChoice = MessageBox.Show(owner,
                    $"Složka '{newFolder}' neodpovídá složce, ke které patří evidence {totalTracked} stažených položek.\r\n\r\n" +
                    "• ANO = použít tuto složku a dosavadní evidenci odpojit od mazání (v Google Photos se NIC nesmaže).\r\n" +
                    "• NE = zrušit a zvolit jinou složku.",
                    "Složka neodpovídá evidenci",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (unlinkChoice != DialogResult.Yes)
                    return false;

                int unlinked = db.UnlinkDownloadedItemsFromDeletion();
                config.InitializeFolderMarker(newFolder, generateNewId: true);
                Logger.Info($"Při obnově konfigurace byla zvolena složka '{newFolder}' mimo evidenci ({unlinked} položek odpojeno od mazání).");
            }

            db?.SetState(SyncEngine.StateKeyFolderId, config.FolderId);
            return true;
        }

        if (totalTracked > 0 && db != null)
        {
            var choice = MessageBox.Show(
                owner,
                $"Změnili jste cílovou složku pro synchronizaci:\r\n" +
                $"Původní: {oldFolder}\r\n" +
                $"Nová: {newFolder}\r\n\r\n" +
                $"V evidenci je {totalTracked} dříve stažených položek.\r\n\r\n" +
                $"• Klikněte na ANO pro automatické PŘESUNUTÍ dříve stažených souborů ze staré složky do nové složky.\r\n" +
                $"• Klikněte na NE pro PŘEPOJENÍ evidence na novou složku bez přesunu souborů (soubory ve staré složce zůstanou na svém místě a v Google Photos se NIC nesmaže).\r\n" +
                $"• Klikněte na STORNO pro zrušení změny složky.",
                "Změna cílové složky – ochrana před smazáním fotek",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (choice == DialogResult.Cancel)
            {
                return false;
            }

            Directory.CreateDirectory(newFolder);

            if (choice == DialogResult.Yes)
            {
                int movedCount = 0;
                db.RevertPendingRemoteDeletesToDownloaded();
                foreach (var item in db.GetItemsByStatus(SyncItemStatus.Downloaded))
                {
                    if (string.IsNullOrWhiteSpace(item.LocalFileName)) continue;
                    var srcPath = Path.Combine(oldFolder, item.LocalFileName);
                    var dstPath = Path.Combine(newFolder, item.LocalFileName);
                    try
                    {
                        if (File.Exists(srcPath))
                        {
                            var dstDir = Path.GetDirectoryName(dstPath);
                            if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
                            if (!File.Exists(dstPath))
                            {
                                File.Move(srcPath, dstPath);
                                movedCount++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Nelze přesunout soubor '{item.LocalFileName}' do nové složky: {ex.Message}");
                    }
                }

                // Pokud v původní složce některé soubory již nebyly, odpojíme je od mazání, aby změna složky nikdy nevyvolala mazání
                foreach (var item in db.GetItemsByStatus(SyncItemStatus.Downloaded))
                {
                    var dstPath = Path.Combine(newFolder, item.LocalFileName);
                    if (!File.Exists(dstPath))
                    {
                        db.UpdateStatus(item.MediaKey, SyncItemStatus.SkippedInitial, SkipReason.FolderUnlinked);
                    }
                }

                config.DownloadFolder = newFolder;
                config.InitializeFolderMarker(newFolder, generateNewId: true);
                db.SetState(SyncEngine.StateKeyFolderId, config.FolderId);
                Logger.Info($"Cílová složka změněna na '{newFolder}' (přesunuto {movedCount} souborů).");
                return true;
            }
            else
            {
                int unlinked = db.UnlinkDownloadedItemsFromDeletion();
                config.DownloadFolder = newFolder;
                config.InitializeFolderMarker(newFolder, generateNewId: true);
                db.SetState(SyncEngine.StateKeyFolderId, config.FolderId);
                Logger.Info($"Cílová složka změněna na '{newFolder}' (evidence {unlinked} starších položek byla bezpečně odpojena od mazání).");
                return true;
            }
        }

        config.DownloadFolder = newFolder;
        config.InitializeFolderMarker(newFolder, generateNewId: true);
        db?.SetState(SyncEngine.StateKeyFolderId, config.FolderId);
        return true;
    }

    private async Task<bool> SaveFieldsToConfigAsync()
    {
        // Bod 4: Před uložením změny složky vždy zrušit a počkat na doběhnutí synchronizace i mazání
        await _syncEngine.StopAndWaitForIdleAsync();
        try
        {
            return SaveFieldsToConfigCore();
        }
        finally
        {
            // Časovače a sledování složky obnovit i při zrušení změny složky nebo chybě
            _syncEngine.UpdateConfiguration();
        }
    }

    private bool SaveFieldsToConfigCore()
    {
        bool videosNewlyEnabled = !_config.DownloadVideos && _chkVideos.Checked;

        if (!ApplyFolderChangeSafely(this, _config, _syncEngine.Database, _txtFolder.Text))
        {
            return false;
        }

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
        _config.IsFirstRunCompleted = true;

        // Bod 12: Umožnit uživateli po ověření/potvrzení složky přímo v aplikaci odblokovat chybový stav configu
        if (_config.HasConfigLoadError)
        {
            _config.ClearConfigErrorAndSave();
        }
        else
        {
            _config.Save();
        }

        // P1-12: Pokud uživatel zapnul stahování videí v Nastavení, uvolníme dříve přeskočená videa
        if (videosNewlyEnabled)
        {
            int freedVideos = _syncEngine.Database.ClearSkippedVideos();
            if (freedVideos > 0)
            {
                Logger.Info($"Zapnuto stahování videí – uvolněno {freedVideos} dříve přeskočených videí ke stažení.");
            }
        }

        return true;
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        bool videosNewlyEnabled = !_config.DownloadVideos && _chkVideos.Checked;
        if (!await SaveFieldsToConfigAsync())
            return;

        StartupManager.SetStartWithWindows(_config.StartWithWindows);
        _syncEngine.UpdateConfiguration();

        DialogResult = DialogResult.OK;
        Close();

        if (videosNewlyEnabled)
        {
            await _syncEngine.RunSyncCycleAsync(manualTrigger: true);
        }
    }
}

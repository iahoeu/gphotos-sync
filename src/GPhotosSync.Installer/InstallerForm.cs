using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace GPhotosSync.Installer;

public class InstallerForm : Form
{
    private int _currentPage = 0;
    private readonly Panel _pageWelcome;
    private readonly Panel _pageSyncOptions;
    private readonly Panel _pageProgress;
    private readonly Panel _pageFinish;

    private readonly Button _btnBack;
    private readonly Button _btnNext;
    private readonly Button _btnCancel;

    private readonly TextBox _txtInstallDir;
    private readonly CheckBox _chkDesktopShortcut;
    private readonly CheckBox _chkStartMenuShortcut;
    private readonly CheckBox _chkStartWithWindows;

    private readonly TextBox _txtPhotosFolder;
    private readonly RadioButton _rbOnlyNew;
    private readonly RadioButton _rbLast7Days;
    private readonly RadioButton _rbLast30Days;
    private readonly RadioButton _rbAllHistory;
    private readonly CheckBox _chkVideos;
    private readonly CheckBox _chkTwoWayDelete;

    private readonly ProgressBar _progressBar;
    private readonly Label _lblProgressStatus;
    private readonly CheckBox _chkLaunchNow;

    public InstallerForm()
    {
        Text = "Instalace – Google Photos Sync pro Windows (v1.0)";
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath); } catch { }
        Width = 640;
        Height = 510;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.White;

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 72,
            BackColor = Color.FromArgb(26, 115, 232)
        };

        var lblHeaderTitle = new Label
        {
            Text = "Průvodce instalací Google Photos Sync",
            Font = new Font("Segoe UI Semibold", 13.5f, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(22, 12),
            AutoSize = true
        };

        var lblHeaderSub = new Label
        {
            Text = "Automatická synchronizace fotek a videí z Google Photos do Windows + obousměrné mazání",
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.FromArgb(225, 238, 255),
            Location = new Point(24, 41),
            AutoSize = true
        };

        header.Controls.Add(lblHeaderTitle);
        header.Controls.Add(lblHeaderSub);

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            BackColor = Color.FromArgb(242, 245, 250)
        };

        _btnBack = new Button
        {
            Text = "< Zpět",
            Location = new Point(310, 14),
            Size = new Size(95, 32),
            Enabled = false
        };
        _btnBack.Click += (_, _) => ShowPage(_currentPage - 1);

        _btnNext = new Button
        {
            Text = "Další >",
            Location = new Point(415, 14),
            Size = new Size(105, 32),
            BackColor = Color.FromArgb(26, 115, 232),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _btnNext.FlatAppearance.BorderSize = 0;
        _btnNext.Click += async (_, _) => await OnNextClickedAsync();

        _btnCancel = new Button
        {
            Text = "Storno",
            Location = new Point(530, 14),
            Size = new Size(85, 32)
        };
        _btnCancel.Click += (_, _) => Close();

        footer.Controls.Add(_btnBack);
        footer.Controls.Add(_btnNext);
        footer.Controls.Add(_btnCancel);

        // PAGE 0: Welcome & Install Path
        _pageWelcome = CreatePagePanel();
        var lblWelcome = new Label
        {
            Text = "Tento průvodce nainstaluje aplikaci Google Photos Sync do vašeho počítače.\r\n\r\n" +
                   "Aplikace běží nenápadně na pozadí u hodin (System Tray), automaticky stahuje nové fotky a videa vyfocené telefonem z Google Photos do počítače a při smazání či přesunutí fotky na PC ji umí hodit do koše i v Google Photos.",
            Location = new Point(24, 18),
            Size = new Size(575, 85)
        };

        var grpInstallDir = new GroupBox
        {
            Text = "Cílová složka instalace programu",
            Location = new Point(24, 112),
            Size = new Size(575, 78)
        };

        _txtInstallDir = new TextBox
        {
            Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GPhotosSync"),
            Location = new Point(16, 32),
            Size = new Size(445, 27)
        };

        var btnBrowseInstall = new Button
        {
            Text = "Procházet...",
            Location = new Point(468, 30),
            Size = new Size(92, 29)
        };
        btnBrowseInstall.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _txtInstallDir.Text };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _txtInstallDir.Text = dlg.SelectedPath;
        };

        grpInstallDir.Controls.Add(_txtInstallDir);
        grpInstallDir.Controls.Add(btnBrowseInstall);

        _chkDesktopShortcut = new CheckBox
        {
            Text = "Vytvořit zástupce na Ploše",
            Checked = true,
            Location = new Point(28, 206),
            AutoSize = true
        };

        _chkStartMenuShortcut = new CheckBox
        {
            Text = "Vytvořit zástupce v nabídce Start",
            Checked = true,
            Location = new Point(28, 238),
            AutoSize = true
        };

        _chkStartWithWindows = new CheckBox
        {
            Text = "Spouštět aplikaci automaticky po zapnutí Windows (běh na pozadí)",
            Checked = true,
            Location = new Point(28, 270),
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold)
        };

        _pageWelcome.Controls.Add(lblWelcome);
        _pageWelcome.Controls.Add(grpInstallDir);
        _pageWelcome.Controls.Add(_chkDesktopShortcut);
        _pageWelcome.Controls.Add(_chkStartMenuShortcut);
        _pageWelcome.Controls.Add(_chkStartWithWindows);

        // PAGE 1: Sync Options & Initial Sync Mode
        _pageSyncOptions = CreatePagePanel();

        var grpPhotosFolder = new GroupBox
        {
            Text = "Kam ukládat stažené fotky a videa v počítači (jedna společná složka)",
            Location = new Point(24, 14),
            Size = new Size(575, 74)
        };

        _txtPhotosFolder = new TextBox
        {
            Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Google Photos"),
            Location = new Point(16, 30),
            Size = new Size(445, 27)
        };

        var btnBrowsePhotos = new Button
        {
            Text = "Procházet...",
            Location = new Point(468, 28),
            Size = new Size(92, 29)
        };
        btnBrowsePhotos.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _txtPhotosFolder.Text };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _txtPhotosFolder.Text = dlg.SelectedPath;
        };

        grpPhotosFolder.Controls.Add(_txtPhotosFolder);
        grpPhotosFolder.Controls.Add(btnBrowsePhotos);

        var grpInitialMode = new GroupBox
        {
            Text = "Počáteční synchronizace – co se má stáhnout po instalaci?",
            Location = new Point(24, 98),
            Size = new Size(575, 140)
        };

        _rbOnlyNew = new RadioButton
        {
            Text = "Pouze nové fotky vyfocené od této chvíle (Doporučeno – nestahuje staré fotky)",
            Checked = true,
            Location = new Point(16, 26),
            AutoSize = true
        };

        _rbLast7Days = new RadioButton
        {
            Text = "Fotky a videa za posledních 7 dní",
            Location = new Point(16, 54),
            AutoSize = true
        };

        _rbLast30Days = new RadioButton
        {
            Text = "Fotky a videa za posledních 30 dní",
            Location = new Point(16, 82),
            AutoSize = true
        };

        _rbAllHistory = new RadioButton
        {
            Text = "Stáhnout celou dostupnou historii z Google Photos",
            Location = new Point(16, 110),
            AutoSize = true
        };

        grpInitialMode.Controls.Add(_rbOnlyNew);
        grpInitialMode.Controls.Add(_rbLast7Days);
        grpInitialMode.Controls.Add(_rbLast30Days);
        grpInitialMode.Controls.Add(_rbAllHistory);

        _chkVideos = new CheckBox
        {
            Text = "Stahovat kromě fotek také videa v plné originální kvalitě",
            Checked = true,
            Location = new Point(26, 248),
            AutoSize = true
        };

        _chkTwoWayDelete = new CheckBox
        {
            Text = "Obousměrná synchronizace: Při smazání nebo přesunutí fotky ze složky v PC ji přesunout do koše i v Google Photos",
            Checked = true,
            Location = new Point(26, 280),
            Size = new Size(575, 42),
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(20, 60, 130)
        };

        _pageSyncOptions.Controls.Add(grpPhotosFolder);
        _pageSyncOptions.Controls.Add(grpInitialMode);
        _pageSyncOptions.Controls.Add(_chkVideos);
        _pageSyncOptions.Controls.Add(_chkTwoWayDelete);

        // PAGE 2: Installing Progress
        _pageProgress = CreatePagePanel();
        _lblProgressStatus = new Label
        {
            Text = "Připravuji instalaci...",
            Location = new Point(24, 80),
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold)
        };

        _progressBar = new ProgressBar
        {
            Location = new Point(24, 115),
            Size = new Size(575, 28),
            Minimum = 0,
            Maximum = 100
        };

        _pageProgress.Controls.Add(_lblProgressStatus);
        _pageProgress.Controls.Add(_progressBar);

        // PAGE 3: Finish
        _pageFinish = CreatePagePanel();
        var lblFinishTitle = new Label
        {
            Text = "✓ Instalace Google Photos Sync byla úspěšně dokončena!",
            Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold),
            ForeColor = Color.FromArgb(22, 135, 60),
            Location = new Point(24, 30),
            AutoSize = true
        };

        var lblFinishDesc = new Label
        {
            Text = "Aplikace je nainstalována a připravena. Po kliknutí na tlačítko 'Dokončit' se aplikace spustí a budete se moci jednorázově přihlásit k účtu Google Photos.",
            Location = new Point(24, 72),
            Size = new Size(570, 55)
        };

        _chkLaunchNow = new CheckBox
        {
            Text = "Spustit Google Photos Sync nyní",
            Checked = true,
            Location = new Point(26, 145),
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold)
        };

        _pageFinish.Controls.Add(lblFinishTitle);
        _pageFinish.Controls.Add(lblFinishDesc);
        _pageFinish.Controls.Add(_chkLaunchNow);

        Controls.Add(_pageWelcome);
        Controls.Add(_pageSyncOptions);
        Controls.Add(_pageProgress);
        Controls.Add(_pageFinish);
        Controls.Add(header);
        Controls.Add(footer);

        ShowPage(0);
    }

    private static Panel CreatePagePanel()
    {
        return new Panel
        {
            Dock = DockStyle.Fill,
            Visible = false
        };
    }

    private void ShowPage(int pageIndex)
    {
        _currentPage = pageIndex;
        _pageWelcome.Visible = pageIndex == 0;
        _pageSyncOptions.Visible = pageIndex == 1;
        _pageProgress.Visible = pageIndex == 2;
        _pageFinish.Visible = pageIndex == 3;

        _btnBack.Enabled = pageIndex == 1;
        if (pageIndex == 0)
        {
            _btnNext.Enabled = true;
            _btnCancel.Enabled = true;
            _btnNext.Text = "Další >";
            _btnNext.BackColor = Color.FromArgb(26, 115, 232);
        }
        else if (pageIndex == 1)
        {
            // Bod 14: Při přechodu nebo návratu tlačítkem Zpět vždy znovu povolit tlačítko Instalovat / Další
            _btnNext.Enabled = true;
            _btnCancel.Enabled = true;
            _btnNext.Text = "Instalovat";
            _btnNext.BackColor = Color.FromArgb(24, 138, 66);
        }
        else if (pageIndex == 2)
        {
            _btnBack.Enabled = false;
            _btnNext.Enabled = false;
            _btnCancel.Enabled = false;
        }
        else if (pageIndex == 3)
        {
            _btnBack.Visible = false;
            _btnCancel.Visible = false;
            _btnNext.Enabled = true;
            _btnNext.Text = "Dokončit";
            _btnNext.Location = new Point(510, 14);
        }
    }

    /// <summary>
    /// Bod 2 (P0): Ověří, že instalační složka není shodná se složkou fotek ani její nadřazenou/podřazenou složkou,
    /// že %LocalAppData%\GPhotosSync nezasahuje do složky fotek a že žádná cesta není kořenem disku.
    /// </summary>
    public static (bool IsValid, string? ErrorMessage) ValidateInstallationPaths(string installDirRaw, string photosDirRaw, string appDataDirRaw)
    {
        var installDir = (installDirRaw ?? "").Trim();
        var photosDir = (photosDirRaw ?? "").Trim();
        var appDataDir = (appDataDirRaw ?? "").Trim();

        if (string.IsNullOrWhiteSpace(installDir) || Program.IsDriveRoot(installDir))
        {
            return (false, "Vyberte prosím platnou podsložku pro instalaci programu (nelze instalovat přímo do kořene disku).");
        }

        if (string.IsNullOrWhiteSpace(photosDir) || Program.IsDriveRoot(photosDir))
        {
            return (false, "Vyberte prosím platnou podsložku pro ukládání fotek (nelze použít přímo kořen disku).");
        }

        if (Program.ArePathsOverlapping(installDir, photosDir))
        {
            return (false,
                "Instalační složka programu nesmí být shodná se složkou pro fotky ani nesmí být její nadřazenou či podřazenou složkou.\r\n\r\n" +
                $"Instalační složka: {installDir}\r\n" +
                $"Složka pro fotky: {photosDir}");
        }

        if (!string.IsNullOrWhiteSpace(appDataDir) && Program.ArePathsOverlapping(appDataDir, photosDir))
        {
            return (false,
                $"Složka pro fotky se nesmí překrývat s datovou složkou aplikace ({appDataDir}).\r\n" +
                "Zvolte prosím pro fotky jinou složku (např. ve složce Obrázky).");
        }

        return (true, null);
    }

    private async Task OnNextClickedAsync()
    {
        if (_currentPage == 0)
        {
            var installDir = _txtInstallDir.Text.Trim();
            if (string.IsNullOrWhiteSpace(installDir) || Program.IsDriveRoot(installDir))
            {
                MessageBox.Show(
                    this,
                    "Vyberte prosím platnou podsložku pro instalaci programu (nelze instalovat přímo do kořene disku).",
                    "Neplatná složka instalace",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
            ShowPage(1);
        }
        else if (_currentPage == 1 || _currentPage == 2)
        {
            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GPhotosSync");

            var validation = ValidateInstallationPaths(_txtInstallDir.Text, _txtPhotosFolder.Text, appDataDir);
            if (!validation.IsValid)
            {
                MessageBox.Show(
                    this,
                    validation.ErrorMessage,
                    "Nepovolená kombinace složek",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            ShowPage(2);
            if (await PerformInstallationAsync())
            {
                ShowPage(3);
            }
        }
        else if (_currentPage == 3)
        {
            if (_chkLaunchNow.Checked)
            {
                var exePath = Path.Combine(_txtInstallDir.Text.Trim(), "GPhotosSync.exe");
                if (File.Exists(exePath))
                {
                    Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
                }
            }
            Close();
        }
    }

    private async Task<bool> PerformInstallationAsync()
    {
        try
        {
            _lblProgressStatus.ForeColor = SystemColors.ControlText;
            var installDir = Path.GetFullPath(_txtInstallDir.Text.Trim());
            var photosDir = Path.GetFullPath(_txtPhotosFolder.Text.Trim());
            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GPhotosSync");

            var pathCheck = ValidateInstallationPaths(installDir, photosDir, appDataDir);
            if (!pathCheck.IsValid)
                throw new InvalidOperationException(pathCheck.ErrorMessage);

            // Stop any running instance of GPhotosSync before overwriting files
            _lblProgressStatus.Text = "Ukončuji případné běžící instance aplikace...";
            _progressBar.Value = 10;
            await Task.Run(() =>
            {
                foreach (var proc in Process.GetProcessesByName("GPhotosSync"))
                {
                    try { proc.Kill(); proc.WaitForExit(3000); } catch { }
                }
            });

            _lblProgressStatus.Text = "Rozbaluji soubory programu do cílové složky...";
            _progressBar.Value = 35;

            await Task.Run(() =>
            {
                Directory.CreateDirectory(installDir);
                Directory.CreateDirectory(photosDir);

                using var stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("GPhotosSync.Installer.payload.zip");

                if (stream == null)
                    throw new InvalidOperationException("Chybí vložený instalační balíček payload.zip.");

                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                archive.ExtractToDirectory(installDir, overwriteFiles: true);

                // Copy installer itself as Uninstall.exe for clean removal
                var selfPath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(selfPath) && File.Exists(selfPath))
                {
                    File.Copy(selfPath, Path.Combine(installDir, "Uninstall.exe"), overwrite: true);
                }
            });

            _lblProgressStatus.Text = "Ukládám počáteční nastavení synchronizace...";
            _progressBar.Value = 65;

            string initialModeStr = _rbOnlyNew.Checked ? "only_new"
                : _rbLast7Days.Checked ? "last_7_days"
                : _rbLast30Days.Checked ? "last_30_days"
                : "all";

            Directory.CreateDirectory(appDataDir);

            var defaultsObj = new
            {
                DownloadFolder = photosDir,
                TwoWayDelete = _chkTwoWayDelete.Checked,
                DownloadVideos = _chkVideos.Checked,
                InitialMode = initialModeStr
            };
            File.WriteAllText(
                Path.Combine(appDataDir, "installer_defaults.json"),
                JsonSerializer.Serialize(defaultsObj, new JsonSerializerOptions { WriteIndented = true }));

            _lblProgressStatus.Text = "Vytvářím zástupce a registruji automatické spouštění...";
            _progressBar.Value = 85;

            var targetExe = Path.Combine(installDir, "GPhotosSync.exe");

            if (_chkDesktopShortcut.Checked)
            {
                var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                CreateShortcut(Path.Combine(desktopDir, "Google Photos Sync.lnk"), targetExe, installDir);
            }

            if (_chkStartMenuShortcut.Checked)
            {
                var startMenuDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    "Google Photos Sync");
                Directory.CreateDirectory(startMenuDir);
                CreateShortcut(Path.Combine(startMenuDir, "Google Photos Sync.lnk"), targetExe, installDir);
            }

            // Configure Windows Startup
            using (var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
            {
                if (_chkStartWithWindows.Checked)
                    runKey?.SetValue("GPhotosSync", $"\"{targetExe}\" --background");
                else
                    runKey?.DeleteValue("GPhotosSync", throwOnMissingValue: false);
            }

            // Register in Windows "Installed Apps / Add or Remove Programs"
            RegisterUninstaller(installDir, targetExe);

            _lblProgressStatus.Text = "Instalace dokončena.";
            _progressBar.Value = 100;
            await Task.Delay(400);
            return true;
        }
        catch (Exception ex)
        {
            _lblProgressStatus.Text = $"Instalace selhala: {ex.Message}";
            _lblProgressStatus.ForeColor = Color.FromArgb(190, 35, 35);
            // Bod 14: Povolit návrat tlačítkem Zpět i přímé opakování tlačítkem Instalovat
            _btnBack.Enabled = true;
            _btnNext.Enabled = true;
            _btnNext.Text = "Zkusit znovu";
            _btnCancel.Enabled = true;

            MessageBox.Show(
                $"Během instalace došlo k chybě:\n{ex.Message}",
                "Chyba instalace",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetExe, string workingDir)
    {
        try
        {
            // Use PowerShell WScript.Shell COM to create standard Windows .lnk shortcut cleanly
            var psScript = $@"
                $ws = New-Object -ComObject WScript.Shell;
                $s = $ws.CreateShortcut('{shortcutPath.Replace("'", "''")}');
                $s.TargetPath = '{targetExe.Replace("'", "''")}';
                $s.WorkingDirectory = '{workingDir.Replace("'", "''")}';
                $s.Description = 'Synchronizace fotek a videí z Google Photos';
                $s.Save();
            ";
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -WindowStyle Hidden -Command \"{psScript}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            p?.WaitForExit(4000);
        }
        catch
        {
            // Ignore shortcut creation issues
        }
    }

    private static void RegisterUninstaller(string installDir, string targetExe)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\GPhotosSync");
            if (key == null) return;

            var ver = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            var verStr = $"{ver.Major}.{ver.Minor}.{Math.Max(0, ver.Build)}";
            var uninstallerExe = Path.Combine(installDir, "Uninstall.exe");
            key.SetValue("DisplayName", "Google Photos Sync pro Windows");
            key.SetValue("DisplayVersion", verStr);
            key.SetValue("Publisher", "GPhotosSync");
            key.SetValue("DisplayIcon", targetExe);
            key.SetValue("InstallLocation", installDir);
            key.SetValue("UninstallString", $"\"{uninstallerExe}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        catch
        {
            // Ignore
        }
    }
}

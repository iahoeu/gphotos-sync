using GPhotosSync.Data;
using GPhotosSync.Services;

namespace GPhotosSync.Forms;

public class SetupWizardForm : Form
{
    private readonly AppConfig _config;
    private readonly GooglePhotosEngine _photosEngine;
    private readonly SyncDatabase? _db;

    private readonly Label _lblAccountStatus;
    private readonly Button _btnLogin;
    private readonly TextBox _txtFolder;
    private readonly CheckBox _chkVideos;
    private readonly CheckBox _chkTwoWayDelete;
    private readonly CheckBox _chkAutoStart;

    private readonly RadioButton _rbOnlyNew;
    private readonly RadioButton _rbLastDays;
    private readonly NumericUpDown _numDays;
    private readonly RadioButton _rbFromDate;
    private readonly DateTimePicker _dtpFromDate;
    private readonly RadioButton _rbAllHistory;

    public SetupWizardForm(AppConfig config, GooglePhotosEngine photosEngine, SyncDatabase? db = null)
    {
        _config = config;
        _photosEngine = photosEngine;
        _db = db;

        Text = "Průvodce nastavením – Google Photos Sync";
        Width = 680;
        Height = 650;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.White;

        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 76,
            BackColor = Color.FromArgb(26, 115, 232),
            Padding = new Padding(22, 14, 22, 10)
        };

        var lblTitle = new Label
        {
            Text = "Vítejte v Google Photos Sync pro Windows",
            Font = new Font("Segoe UI Semibold", 14f, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(20, 12)
        };

        var lblSubtitle = new Label
        {
            Text = "Automatické stahování fotek a videí z mobilu (Google Photos) do počítače + obousměrné mazání",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = Color.FromArgb(225, 238, 255),
            AutoSize = true,
            Location = new Point(22, 43)
        };

        headerPanel.Controls.Add(lblTitle);
        headerPanel.Controls.Add(lblSubtitle);

        var body = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(22, 15, 22, 10)
        };

        // 1. Account Section
        var grpAccount = new GroupBox
        {
            Text = "1. Přihlášení k účtu Google Photos",
            Location = new Point(22, 12),
            Size = new Size(620, 78)
        };

        _lblAccountStatus = new Label
        {
            Text = "Kontroluji stav přihlášení...",
            Location = new Point(16, 32),
            Size = new Size(390, 26),
            ForeColor = Color.DimGray
        };

        _btnLogin = new Button
        {
            Text = "Přihlásit účet Google...",
            Location = new Point(420, 26),
            Size = new Size(182, 34),
            BackColor = Color.FromArgb(26, 115, 232),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        _btnLogin.FlatAppearance.BorderSize = 0;
        _btnLogin.Click += async (_, _) => await OpenLoginDialogAsync();

        grpAccount.Controls.Add(_lblAccountStatus);
        grpAccount.Controls.Add(_btnLogin);

        // 2. Folder & Sync Behavior Section
        var grpFolder = new GroupBox
        {
            Text = "2. Cílová složka a chování synchronizace",
            Location = new Point(22, 100),
            Size = new Size(620, 185)
        };

        var lblFolderDesc = new Label
        {
            Text = "Všechny fotky a videa se ukládají do jedné přehledné složky (se zachováním EXIF metadat):",
            Location = new Point(16, 26),
            AutoSize = true
        };

        _txtFolder = new TextBox
        {
            Text = _config.DownloadFolder,
            Location = new Point(16, 50),
            Size = new Size(475, 27)
        };

        var btnBrowse = new Button
        {
            Text = "Vybrat...",
            Location = new Point(500, 48),
            Size = new Size(102, 29)
        };
        btnBrowse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Vyberte složku pro ukládání fotek z Google Photos",
                SelectedPath = _txtFolder.Text,
                UseDescriptionForTitle = true
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _txtFolder.Text = dlg.SelectedPath;
            }
        };

        _chkVideos = new CheckBox
        {
            Text = "Stahovat kromě fotek také videa (v plné originální kvalitě)",
            Checked = _config.DownloadVideos,
            Location = new Point(16, 88),
            AutoSize = true
        };

        _chkTwoWayDelete = new CheckBox
        {
            Text = "Obousměrná synchronizace: Při smazání nebo přesunutí fotky ze složky v PC ji hodit do koše i v Google Photos",
            Checked = _config.TwoWayDelete,
            Location = new Point(16, 118),
            Size = new Size(595, 26),
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(20, 60, 130)
        };

        _chkAutoStart = new CheckBox
        {
            Text = "Spouštět aplikaci automaticky po zapnutí počítače (běh na pozadí u hodin)",
            Checked = _config.StartWithWindows,
            Location = new Point(16, 148),
            AutoSize = true
        };

        grpFolder.Controls.Add(lblFolderDesc);
        grpFolder.Controls.Add(_txtFolder);
        grpFolder.Controls.Add(btnBrowse);
        grpFolder.Controls.Add(_chkVideos);
        grpFolder.Controls.Add(_chkTwoWayDelete);
        grpFolder.Controls.Add(_chkAutoStart);

        // 3. Initial Sync Choice Section
        var grpInitial = new GroupBox
        {
            Text = "3. Počáteční synchronizace (co se má stáhnout při prvním spuštění)",
            Location = new Point(22, 296),
            Size = new Size(620, 165)
        };

        _rbOnlyNew = new RadioButton
        {
            Text = "Pouze nové fotky vyfocené od této chvíle (Doporučeno – nestahuje starou historii)",
            Checked = _config.InitialMode == InitialSyncMode.OnlyNewFromNow,
            Location = new Point(16, 28),
            AutoSize = true
        };

        _rbLastDays = new RadioButton
        {
            Text = "Fotky a videa za posledních",
            Checked = _config.InitialMode == InitialSyncMode.LastDays,
            Location = new Point(16, 60),
            AutoSize = true
        };

        _numDays = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 3650,
            Value = Math.Clamp(_config.InitialSyncDays, 1, 3650),
            Location = new Point(225, 58),
            Width = 65
        };

        var lblDaysSuffix = new Label
        {
            Text = "dní",
            Location = new Point(296, 61),
            AutoSize = true
        };

        _rbFromDate = new RadioButton
        {
            Text = "Fotky a videa vyfocené od data:",
            Checked = _config.InitialMode == InitialSyncMode.FromDate,
            Location = new Point(16, 94),
            AutoSize = true
        };

        _dtpFromDate = new DateTimePicker
        {
            Format = DateTimePickerFormat.Short,
            Value = _config.InitialSyncFromDate,
            Location = new Point(245, 91),
            Width = 135
        };

        _rbAllHistory = new RadioButton
        {
            Text = "Stáhnout celou dostupnou historii z Google Photos",
            Checked = _config.InitialMode == InitialSyncMode.AllHistory,
            Location = new Point(16, 128),
            AutoSize = true
        };

        grpInitial.Controls.Add(_rbOnlyNew);
        grpInitial.Controls.Add(_rbLastDays);
        grpInitial.Controls.Add(_numDays);
        grpInitial.Controls.Add(lblDaysSuffix);
        grpInitial.Controls.Add(_rbFromDate);
        grpInitial.Controls.Add(_dtpFromDate);
        grpInitial.Controls.Add(_rbAllHistory);

        body.Controls.Add(grpAccount);
        body.Controls.Add(grpFolder);
        body.Controls.Add(grpInitial);

        // Footer buttons
        var footerPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 62,
            BackColor = Color.FromArgb(245, 247, 250),
            Padding = new Padding(22, 12, 22, 12)
        };

        var btnFinish = new Button
        {
            Text = "Uložit a spustit synchronizaci",
            Dock = DockStyle.Right,
            Width = 240,
            BackColor = Color.FromArgb(24, 138, 66),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnFinish.FlatAppearance.BorderSize = 0;
        btnFinish.Click += OnFinishClicked;

        footerPanel.Controls.Add(btnFinish);

        Controls.Add(body);
        Controls.Add(headerPanel);
        Controls.Add(footerPanel);

        Shown += async (_, _) => await RefreshLoginStatusAsync();
    }

    private async Task RefreshLoginStatusAsync(bool offerManualEmailPromptIfMissing = false)
    {
        _lblAccountStatus.Text = "Ověřuji přihlášení ke Google Photos...";
        _lblAccountStatus.ForeColor = Color.DimGray;

        var check = await _photosEngine.CheckLoginAndAccountAsync(_config.ExpectedGoogleAccountEmail);
        if (check.IsLoggedIn)
        {
            if (string.IsNullOrWhiteSpace(_config.ExpectedGoogleAccountEmail) &&
                !string.IsNullOrWhiteSpace(check.DetectedEmail))
            {
                _config.ExpectedGoogleAccountEmail = check.DetectedEmail;
            }
            else if (offerManualEmailPromptIfMissing &&
                     string.IsNullOrWhiteSpace(check.DetectedEmail) &&
                     string.IsNullOrWhiteSpace(_config.ExpectedGoogleAccountEmail))
            {
                SettingsForm.PromptManualGoogleAccountConfirmation(this, _config, null, _photosEngine);
            }

            var effectiveEmail = !string.IsNullOrWhiteSpace(check.DetectedEmail)
                ? check.DetectedEmail
                : _config.ExpectedGoogleAccountEmail;

            if (!string.IsNullOrWhiteSpace(effectiveEmail))
            {
                _lblAccountStatus.Text = $"✓ Účet Google Photos je připojen a ověřen ({effectiveEmail}).";
                _lblAccountStatus.ForeColor = Color.FromArgb(20, 135, 55);
            }
            else
            {
                _lblAccountStatus.Text = "✓ Přihlášeno (e-mail neověřen – stahování poběží, mazání vypnuto).";
                _lblAccountStatus.ForeColor = Color.FromArgb(195, 110, 15);
            }
            _btnLogin.Text = "Změnit účet Google...";
        }
        else
        {
            _lblAccountStatus.Text = check.ErrorMessage ?? "⚠ Zatím nejste přihlášeni. Klikněte na tlačítko vpravo ->";
            _lblAccountStatus.ForeColor = Color.FromArgb(195, 75, 15);
            _btnLogin.Text = "Přihlásit účet Google...";
        }
    }

    private async Task OpenLoginDialogAsync()
    {
        using var loginForm = new GoogleLoginForm(_photosEngine);
        loginForm.ShowDialog(this);
        if (loginForm.LoginSucceeded)
        {
            // Uživatel vědomě mění/přihlašuje účet -> uvolnit předchozí vazbu na e-mail a načíst/potvrdit nový
            _config.ExpectedGoogleAccountEmail = string.Empty;
        }
        await RefreshLoginStatusAsync(offerManualEmailPromptIfMissing: loginForm.LoginSucceeded);
    }

    private void OnFinishClicked(object? sender, EventArgs e)
    {
        if (!SettingsForm.ApplyFolderChangeSafely(this, _config, _db, _txtFolder.Text))
        {
            return;
        }

        _config.DownloadVideos = _chkVideos.Checked;
        _config.TwoWayDelete = _chkTwoWayDelete.Checked;
        _config.StartWithWindows = _chkAutoStart.Checked;

        if (_rbOnlyNew.Checked)
        {
            _config.InitialMode = InitialSyncMode.OnlyNewFromNow;
            _config.InstalledAt = DateTime.Now;
            _config.BaselineCaptured = false;
        }
        else if (_rbLastDays.Checked)
        {
            _config.InitialMode = InitialSyncMode.LastDays;
            _config.InitialSyncDays = (int)_numDays.Value;
        }
        else if (_rbFromDate.Checked)
        {
            _config.InitialMode = InitialSyncMode.FromDate;
            _config.InitialSyncFromDate = _dtpFromDate.Value.Date;
        }
        else
        {
            _config.InitialMode = InitialSyncMode.AllHistory;
        }

        _config.IsFirstRunCompleted = true;
        if (_config.HasConfigLoadError)
        {
            _config.ClearConfigErrorAndSave();
        }
        else
        {
            _config.Save();
        }

        StartupManager.SetStartWithWindows(_config.StartWithWindows);

        DialogResult = DialogResult.OK;
        Close();
    }
}

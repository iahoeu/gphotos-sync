using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace GPhotosSync.Installer;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            RunUninstaller();
            return;
        }

        int silentIdx = Array.FindIndex(args, a => a.Equals("--silent-update", StringComparison.OrdinalIgnoreCase));
        if (silentIdx >= 0)
        {
            string targetDir = (silentIdx + 1 < args.Length && !string.IsNullOrWhiteSpace(args[silentIdx + 1]))
                ? args[silentIdx + 1]
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GPhotosSync");

            RunSilentUpdate(targetDir);
            return;
        }

        Application.Run(new InstallerForm());
    }

    private static void RunSilentUpdate(string installDir)
    {
        try
        {
            // Wait for running GPhotosSync processes to exit
            for (int wait = 0; wait < 20; wait++)
            {
                var running = Process.GetProcessesByName("GPhotosSync");
                if (running.Length == 0) break;
                foreach (var proc in running)
                {
                    try
                    {
                        if (wait > 5) proc.Kill();
                        else proc.WaitForExit(500);
                    }
                    catch { }
                }
                Thread.Sleep(400);
            }

            Directory.CreateDirectory(installDir);

            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("GPhotosSync.Installer.payload.zip");

            if (stream != null)
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                archive.ExtractToDirectory(installDir, overwriteFiles: true);
            }

            var selfPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(selfPath) && File.Exists(selfPath))
            {
                try { File.Copy(selfPath, Path.Combine(installDir, "Uninstall.exe"), overwrite: true); } catch { }
            }

            var targetExe = Path.Combine(installDir, "GPhotosSync.exe");
            if (File.Exists(targetExe))
            {
                Process.Start(new ProcessStartInfo(targetExe, "--background")
                {
                    UseShellExecute = true,
                    WorkingDirectory = installDir
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Chyba při automatické aktualizaci: {ex.Message}", "Chyba aktualizace", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void RunUninstaller()
    {
        var confirm = MessageBox.Show(
            "Opravdu chcete odinstalovat aplikaci Google Photos Sync z počítače?\r\n(Vaše již stažené fotky ve složce Obrázky zůstanou zachovány.)",
            "Odinstalace Google Photos Sync",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes)
            return;

        try
        {
            foreach (var proc in Process.GetProcessesByName("GPhotosSync"))
            {
                try { proc.Kill(); proc.WaitForExit(3000); } catch { }
            }

            using (var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
            {
                runKey?.DeleteValue("GPhotosSync", throwOnMissingValue: false);
            }

            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\GPhotosSync", throwOnMissingSubKey: false);

            var desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Google Photos Sync.lnk");
            if (File.Exists(desktopShortcut)) File.Delete(desktopShortcut);

            var startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Google Photos Sync");
            if (Directory.Exists(startMenuDir)) Directory.Delete(startMenuDir, recursive: true);

            MessageBox.Show(
                "Aplikace Google Photos Sync byla úspěšně odinstalována.",
                "Odinstalace dokončena",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Chyba při odinstalaci: {ex.Message}", "Chyba", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace GPhotosSync.Installer;

public static class Program
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

    public static bool IsDriveRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;

        try
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrWhiteSpace(full) || string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Bod 2 (P0): Zjistí, zda jsou dvě cesty shodné nebo zda je jedna nadřazenou/podřazenou složkou druhé.
    /// </summary>
    public static bool ArePathsOverlapping(string pathA, string pathB)
    {
        if (string.IsNullOrWhiteSpace(pathA) || string.IsNullOrWhiteSpace(pathB))
            return false;

        try
        {
            var normA = Path.GetFullPath(pathA).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var normB = Path.GetFullPath(pathB).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            return string.Equals(normA, normB, StringComparison.OrdinalIgnoreCase) ||
                   normA.StartsWith(normB, StringComparison.OrdinalIgnoreCase) ||
                   normB.StartsWith(normA, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static void RunSilentUpdate(string installDir)
    {
        try
        {
            if (IsDriveRoot(installDir))
                return;

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

    private static HashSet<string> GetKnownPayloadRelativeFiles()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GPhotosSync.exe",
            "GPhotosSync.dll",
            "GPhotosSync.deps.json",
            "GPhotosSync.runtimeconfig.json",
            "GPhotosSync.pdb",
            "WebView2Loader.dll",
            "Microsoft.Web.WebView2.Core.dll",
            "Microsoft.Web.WebView2.WinForms.dll",
            "Microsoft.Data.Sqlite.dll",
            "SQLitePCLRaw.batteries_v2.dll",
            "SQLitePCLRaw.core.dll",
            "SQLitePCLRaw.provider.e_sqlite3.dll",
            "e_sqlite3.dll"
        };

        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("GPhotosSync.Installer.payload.zip");
            if (stream != null)
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                foreach (var entry in archive.Entries)
                {
                    if (!string.IsNullOrWhiteSpace(entry.Name))
                    {
                        var norm = entry.FullName.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                        files.Add(norm);
                    }
                }
            }
        }
        catch
        {
        }

        return files;
    }

    private static string? TryReadConfiguredPhotosFolder(string appDataDir)
    {
        string[] candidateFiles = ["config.json", "config.json.bak", "installer_defaults.json"];
        foreach (var fileName in candidateFiles)
        {
            try
            {
                var path = Path.Combine(appDataDir, fileName);
                if (!File.Exists(path))
                    continue;

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("DownloadFolder", out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    var val = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                        return val;
                }
            }
            catch
            {
            }
        }
        return null;
    }

    private static void SafeDeleteAppDataDirectory(string appDataDir)
    {
        if (!Directory.Exists(appDataDir) || IsDriveRoot(appDataDir))
            return;

        var photosDir = TryReadConfiguredPhotosFolder(appDataDir);

        // Bod 2: Mazání %LocalAppData%\GPhotosSync nesmí nikdy zasáhnout složku fotek. Umístění fotek nemusí
        // být známé (chybějící nebo poškozený config), proto se vždy mažou jen známé soubory dat aplikace
        // a složka samotná jen tehdy, když po nich zůstane prázdná.
        string[] knownAppDataFiles =
        [
            "config.json",
            "config.json.bak",
            "config.json.tmp",
            "installer_defaults.json",
            "sync_history.db",
            "sync_history.db-wal",
            "sync_history.db-shm",
            "sync_history.db-journal",
            "sync.log"
        ];

        foreach (var f in knownAppDataFiles)
        {
            var fp = Path.Combine(appDataDir, f);
            try { if (File.Exists(fp)) File.Delete(fp); } catch { }
        }

        // Profil WebView2 (cookies přihlášení Google) vytváří výhradně aplikace
        var webViewDir = Path.Combine(appDataDir, "WebViewProfile");
        if (Directory.Exists(webViewDir) &&
            (string.IsNullOrWhiteSpace(photosDir) || !ArePathsOverlapping(webViewDir, photosDir)))
        {
            try
            {
                Directory.Delete(webViewDir, recursive: true);
            }
            catch
            {
                // Podproces WebView2 může ještě krátce držet zámek
                Thread.Sleep(500);
                try { Directory.Delete(webViewDir, recursive: true); } catch { }
            }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(appDataDir).Any())
            {
                Directory.Delete(appDataDir, recursive: false);
            }
        }
        catch { }
    }

    private static void RunUninstaller()
    {
        // Bod 2 (P0): Bez ověřené instalační složky z registru (InstallLocation + existující GPhotosSync.exe)
        // odinstalátor okamžitě skončí. Nikdy nepoužívat AppContext.BaseDirectory!
        string? installDir = null;
        try
        {
            using var uninstallKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\GPhotosSync", writable: false);
            installDir = uninstallKey?.GetValue("InstallLocation") as string;
        }
        catch
        {
        }

        if (string.IsNullOrWhiteSpace(installDir))
        {
            MessageBox.Show(
                "Nelze ověřit instalační složku aplikace (chybí záznam InstallLocation v registru).\r\n" +
                "Odinstalace byla z bezpečnostních důvodů zastavena.",
                "Chyba ověření instalace",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        installDir = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var mainExePath = Path.Combine(installDir, "GPhotosSync.exe");

        if (IsDriveRoot(installDir) || !Directory.Exists(installDir) || !File.Exists(mainExePath))
        {
            MessageBox.Show(
                $"Instalační složka '{installDir}' neobsahuje ověřený soubor 'GPhotosSync.exe'.\r\n" +
                "Odinstalace byla z bezpečnostních důvodů zastavena, aby nedošlo ke smazání cizích souborů.",
                "Chyba ověření instalační složky",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        var confirm = MessageBox.Show(
            "Opravdu chcete odinstalovat aplikaci Google Photos Sync z počítače?\r\n\r\n" +
            "(Vaše stažené fotky a videa ve složce s fotkami zůstanou vždy plně zachovány.)",
            "Odinstalace Google Photos Sync",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes)
            return;

        var deleteAppData = MessageBox.Show(
            "Chcete smazat také uložená aplikační data v profilu Windows (%LocalAppData%\\GPhotosSync)?\r\n\r\n" +
            "• ANO = smaže přihlašovací cookies Google (WebView2), nastavení a databázi synchronizace\r\n" +
            "• NE = ponechá přihlášení a nastavení pro případnou budoucí přeinstalaci\r\n\r\n" +
            "Poznámka: Vašich stažených fotek se tato volba netýká, ty zůstanou zachovány v obou případech.",
            "Smazat přihlašovací údaje a nastavení?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

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

            var startMenuShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Google Photos Sync", "Google Photos Sync.lnk");
            if (File.Exists(startMenuShortcut))
            {
                try { File.Delete(startMenuShortcut); } catch { }
            }
            var startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Google Photos Sync");
            if (Directory.Exists(startMenuDir))
            {
                try { Directory.Delete(startMenuDir, recursive: false); } catch { }
            }

            if (deleteAppData == DialogResult.Yes)
            {
                var appDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GPhotosSync");
                SafeDeleteAppDataDirectory(appDataDir);
            }

            // Bod 2 (P0): Mazat POUZE známé soubory aplikace ze seznamu instalačního balíčku,
            // nikdy rekurzivně celou složku a žádné rmdir /s!
            var selfPath = Environment.ProcessPath ?? string.Empty;
            var uninstallerPath = Path.Combine(installDir, "Uninstall.exe");
            var knownRelativeFiles = GetKnownPayloadRelativeFiles();
            var candidateSubDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var relFile in knownRelativeFiles)
            {
                var targetFile = Path.GetFullPath(Path.Combine(installDir, relFile));
                if (!targetFile.StartsWith(installDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrEmpty(selfPath) &&
                    string.Equals(targetFile, Path.GetFullPath(selfPath), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    if (File.Exists(targetFile))
                    {
                        File.Delete(targetFile);
                    }
                }
                catch { }

                var parentDir = Path.GetDirectoryName(targetFile);
                while (!string.IsNullOrWhiteSpace(parentDir) &&
                       parentDir.Length > installDir.Length &&
                       parentDir.StartsWith(installDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    candidateSubDirs.Add(parentDir);
                    parentDir = Path.GetDirectoryName(parentDir);
                }
            }

            // Smazat pouze PRÁZDNÉ podsložky vytvořené instalátorem (recursive: false), od nejhlubších
            foreach (var subDir in candidateSubDirs.OrderByDescending(d => d.Length))
            {
                try
                {
                    if (Directory.Exists(subDir) && !Directory.EnumerateFileSystemEntries(subDir).Any())
                    {
                        Directory.Delete(subDir, recursive: false);
                    }
                }
                catch { }
            }

            // Pokud Uninstall.exe neběží přímo z installDir, smažeme jej hned
            if (File.Exists(uninstallerPath) &&
                (string.IsNullOrEmpty(selfPath) || !string.Equals(Path.GetFullPath(uninstallerPath), Path.GetFullPath(selfPath), StringComparison.OrdinalIgnoreCase)))
            {
                try { File.Delete(uninstallerPath); } catch { }
            }

            MessageBox.Show(
                "Aplikace Google Photos Sync byla úspěšně odinstalována.",
                "Odinstalace dokončena",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            // Naplánovat smazání samotného Uninstall.exe a odstranění instalační složky POUZE pokud je prázdná (rmdir bez /s!)
            if (Directory.Exists(installDir))
            {
                var cmdArgs = $"/C ping 127.0.0.1 -n 3 > nul & del /f /q \"{uninstallerPath}\" > nul 2>&1 & rmdir \"{installDir}\" > nul 2>&1";
                Process.Start(new ProcessStartInfo("cmd.exe", cmdArgs)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Chyba při odinstalaci: {ex.Message}", "Chyba", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

using GPhotosSync.Data;
using Microsoft.Win32;

namespace GPhotosSync.Services;

public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "GPhotosSync";

    public static void SetStartWithWindows(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    var value = $"\"{exePath}\" --background";
                    if (string.Equals(key.GetValue(AppValueName) as string, value, StringComparison.OrdinalIgnoreCase))
                        return; // beze změny – nezapisovat ani nelogovat při každém startu

                    key.SetValue(AppValueName, value);
                    Logger.Info("Automatické spouštění po startu Windows bylo zapnuto.");
                }
            }
            else
            {
                if (key.GetValue(AppValueName) != null)
                {
                    key.DeleteValue(AppValueName, throwOnMissingValue: false);
                    Logger.Info("Automatické spouštění po startu Windows bylo vypnuto.");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Nelze nastavit spouštění po startu Windows: {ex.Message}");
        }
    }

    public static bool IsStartWithWindowsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(AppValueName) != null;
        }
        catch
        {
            return false;
        }
    }
}

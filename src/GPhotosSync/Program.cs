using GPhotosSync.Data;

namespace GPhotosSync;

internal static class Program
{
    private const string MutexName = @"Local\GPhotosSync_SingleInstance_Mutex_2026";

    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            if (!args.Contains("--background", StringComparer.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "Aplikace Google Photos Sync již běží na pozadí (najdete ji u hodin v pravém dolním rohu obrazovky).",
                    "Google Photos Sync již běží",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Logger.Error($"Neošetřená chyba UI vlákna: {e.Exception}");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Logger.Error($"Kritická chyba aplikace: {e.ExceptionObject}");
        };

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}

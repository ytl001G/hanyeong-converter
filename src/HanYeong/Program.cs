using System.Runtime.InteropServices;

namespace HanYeong;

static class Program
{
    /// <summary>Passed by the Run registry entry, so a Windows start-up launch stays quiet.</summary>
    public const string StartupArg = "--startup";

    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\HanYeongConverter", out bool isFirst);
        using var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\HanYeongConverter.ShowSettings");
        if (!isFirst)
        {
            // Launching it again (e.g. from the Start menu) opens the running copy's settings.
            // We hold the foreground right now; let the running copy take it for its window.
            AllowSetForegroundWindow(ASFW_ANY);
            showSettings.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        // No control exists yet, so WinForms has not installed its context; the controller posts through it.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var tray = new TrayContext(isStartupLaunch: args.Contains(StartupArg));
        ThreadPool.RegisterWaitForSingleObject(showSettings, (_, _) => tray.RequestSettings(), null, Timeout.Infinite, executeOnlyOnce: false);
        Application.Run(tray);
    }

    const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}

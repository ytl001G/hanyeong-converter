namespace HanYeong;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, @"Local\HanYeongConverter", out bool isFirst);
        if (!isFirst) return;

        ApplicationConfiguration.Initialize();
        // No control exists yet, so WinForms has not installed its context; the controller posts through it.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.Run(new TrayContext());
    }
}

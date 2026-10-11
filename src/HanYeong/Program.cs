namespace HanYeong;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        // Placeholder until the tray app lands.
        Application.Run(new ApplicationContext());
    }
}

namespace HanYeong;

sealed class TrayContext : ApplicationContext
{
    readonly NotifyIcon _icon;
    readonly Controller _controller;

    public TrayContext()
    {
        _controller = new Controller();
        _controller.AutoModeToggleRequested += () => _icon!.ShowBalloonTip(1500, "한영 변환기", "자동 모드는 아직 준비 중이에요.", ToolTipIcon.Info);

        var menu = new ContextMenuStrip();
        menu.Items.Add("로그 폴더 열기", null, (_, _) => OpenLogFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "한영 변환기 (Ctrl+Space)",
            ContextMenuStrip = menu,
            Visible = true,
        };
        Log.Write("started");
    }

    static void OpenLogFolder()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Log.FilePath)!);
        System.Diagnostics.Process.Start("explorer.exe", Path.GetDirectoryName(Log.FilePath)!);
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _controller.Dispose();
        Log.Write("stopped");
        base.ExitThreadCore();
    }
}

using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace HanYeong;

sealed class TrayContext : ApplicationContext
{
    readonly Settings _settings = Settings.Load();
    readonly Controller _controller;
    readonly NotifyIcon _icon;
    readonly ToolStripMenuItem _autoItem;
    readonly Icon _onIcon = DrawIcon(Color.FromArgb(37, 99, 235));
    readonly Icon _offIcon = DrawIcon(Color.FromArgb(140, 140, 140));

    public TrayContext()
    {
        _controller = new Controller(_settings);

        _autoItem = new ToolStripMenuItem("자동 모드 (Ctrl+Alt+Space)") { CheckOnClick = true };
        _autoItem.Click += (_, _) => _controller.AutoMode = _autoItem.Checked;

        var menu = new ContextMenuStrip();
        menu.Items.Add(_autoItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("로그 폴더 열기", null, (_, _) => OpenLogFolder());
        menu.Items.Add("종료", null, (_, _) => ExitThread());

        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _controller.AutoMode = !_controller.AutoMode;
        };
        _controller.AutoModeChanged += isOn =>
        {
            ShowState(isOn);
            _icon.ShowBalloonTip(1200, "한영 변환기", isOn ? "자동 모드 켜짐" : "자동 모드 꺼짐", ToolTipIcon.None);
        };
        ShowState(_controller.AutoMode);
        Log.Write("started");
    }

    void ShowState(bool isAutoOn)
    {
        _autoItem.Checked = isAutoOn;
        _icon.Icon = isAutoOn ? _onIcon : _offIcon;
        _icon.Text = $"한영 변환기 · Ctrl+Space 변환\n자동 모드 {(isAutoOn ? "켜짐" : "꺼짐")} (Ctrl+Alt+Space)";
    }

    /// <summary>A rounded square with 한 on it, tinted by state.</summary>
    static Icon DrawIcon(Color background)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var path = new GraphicsPath();
            const int r = 8;
            path.AddArc(0, 0, r * 2, r * 2, 180, 90);
            path.AddArc(31 - r * 2, 0, r * 2, r * 2, 270, 90);
            path.AddArc(31 - r * 2, 31 - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(0, 31 - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using var fill = new SolidBrush(background);
            g.FillPath(fill, path);
            using var font = new Font("Malgun Gothic", 15, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("한", font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
        }
        IntPtr handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    static void OpenLogFolder()
    {
        string dir = Path.GetDirectoryName(Log.FilePath)!;
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start("explorer.exe", dir);
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _controller.Dispose();
        _onIcon.Dispose();
        _offIcon.Dispose();
        Log.Write("stopped");
        base.ExitThreadCore();
    }
}

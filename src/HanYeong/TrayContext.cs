using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace HanYeong;

sealed class TrayContext : ApplicationContext
{
    readonly Settings _settings = Settings.Load();
    readonly FocusTracker _focus = new();
    readonly Controller _controller;
    readonly NotifyIcon _icon;
    readonly ToolStripMenuItem _autoItem;
    SettingsForm? _settingsForm;
    readonly Icon _onIcon = DrawIcon(Color.FromArgb(37, 99, 235));
    readonly Icon _offIcon = DrawIcon(Color.FromArgb(140, 140, 140));

    readonly SynchronizationContext _ui;

    public TrayContext(bool isStartupLaunch)
    {
        _ui = SynchronizationContext.Current!;
        _controller = new Controller(_settings, _focus);

        _autoItem = new ToolStripMenuItem { CheckOnClick = true };
        _autoItem.Click += (_, _) => _controller.AutoMode = _autoItem.Checked;

        var appMenu = new ToolStripMenuItem();
        var appAuto = new ToolStripMenuItem("이 앱에서 자동 모드 끄기") { CheckOnClick = true };
        var appHotkey = new ToolStripMenuItem { CheckOnClick = true };
        appMenu.DropDownItems.Add(appAuto);
        appMenu.DropDownItems.Add(appHotkey);
        appAuto.Click += (_, _) => UpdateAppRule(rule => rule.AutoMode = !appAuto.Checked);
        appHotkey.Click += (_, _) => UpdateAppRule(rule => rule.Hotkey = !appHotkey.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_autoItem);
        menu.Items.Add(appMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Opening += (_, _) =>
        {
            // Clicking the tray makes the taskbar the foreground window, so use the app before it.
            string exe = _focus.LastAppExe;
            appMenu.Visible = exe.Length > 0;
            appMenu.Text = $"{exe} 앱 설정";
            var rule = _settings.Apps.GetValueOrDefault(exe);
            appAuto.Checked = rule?.AutoMode == false;
            appHotkey.Text = $"이 앱에서 {_controller.ConvertKey} 끄기";
            appHotkey.Checked = rule?.Hotkey == false;
        };
        menu.Items.Add("설정…", null, (_, _) => OpenSettings());
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
        // First manual start: say where it went, since it has no window.
        if (!isStartupLaunch && !_settings.HasShownTrayHint)
        {
            _settings.HasShownTrayHint = true;
            _settings.Save();
            _icon.ShowBalloonTip(4000, "한영 변환기 실행 중", "작업 표시줄 오른쪽 아래 '한' 아이콘에 있어요.\n설정은 아이콘 우클릭, 또는 시작 메뉴에서 한 번 더 실행하세요.", ToolTipIcon.Info);
        }
        Log.Write("started");
    }

    /// <summary>Called from another thread when the program is launched a second time.</summary>
    public void RequestSettings() => _ui.Post(_ => OpenSettings(), null);

    void UpdateAppRule(Action<AppRule> change)
    {
        string exe = _focus.LastAppExe;
        if (exe.Length == 0) return;
        var rule = _settings.Apps.GetValueOrDefault(exe) ?? new AppRule();
        change(rule);
        // Rules that are back to defaults are dropped so the settings file stays readable.
        if (rule.AutoMode && rule.Hotkey) _settings.Apps.Remove(exe);
        else _settings.Apps[exe] = rule;
        _settings.Save();
        Log.Write($"app rule {exe}: auto={rule.AutoMode} hotkey={rule.Hotkey}");
    }

    void OpenSettings()
    {
        if (_settingsForm is not null)
        {
            _settingsForm.Activate();
            return;
        }
        using (_settingsForm = new SettingsForm(_settings))
        {
            _settingsForm.Shown += (_, _) => _settingsForm.Activate();
            if (_settingsForm.ShowDialog() == DialogResult.OK)
            {
                _controller.ApplySettings();
                ShowState(_settings.AutoMode);
                Log.Write("settings saved");
            }
        }
        _settingsForm = null;
    }

    void ShowState(bool isAutoOn)
    {
        _autoItem.Checked = isAutoOn;
        _autoItem.Text = $"자동 모드 ({_controller.ToggleKey})";
        _icon.Icon = isAutoOn ? _onIcon : _offIcon;
        // NotifyIcon tooltips are limited to 127 characters.
        _icon.Text = $"한영 변환기 · {_controller.ConvertKey} 변환\n자동 모드 {(isAutoOn ? "켜짐" : "꺼짐")} ({_controller.ToggleKey})";
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
        _focus.Dispose();
        _onIcon.Dispose();
        _offIcon.Dispose();
        Log.Write("stopped");
        base.ExitThreadCore();
    }
}

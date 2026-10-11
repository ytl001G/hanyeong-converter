using Microsoft.Win32;

namespace HanYeong;

/// <summary>Hotkeys, exception words, per-app rules and start-with-Windows.</summary>
sealed class SettingsForm : Form
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "HanYeongConverter";

    readonly Settings _settings;
    readonly HotkeyBox _convertBox;
    readonly HotkeyBox _toggleBox;
    readonly CheckBox _autoCheck;
    readonly CheckBox _startupCheck;
    readonly ListBox _exceptionList;
    readonly DataGridView _appGrid;

    public SettingsForm(Settings settings)
    {
        _settings = settings;
        Text = "한영 변환기 설정";
        Font = new Font("Malgun Gothic", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 560);
        Padding = new Padding(14);

        _convertBox = new HotkeyBox(Hotkey.Parse(settings.ConvertHotkey, Hotkey.DefaultConvert));
        _toggleBox = new HotkeyBox(Hotkey.Parse(settings.AutoToggleHotkey, Hotkey.DefaultAutoToggle));
        _autoCheck = new CheckBox { Text = "자동 모드 켜기", Checked = settings.AutoMode, AutoSize = true };
        _startupCheck = new CheckBox { Text = "Windows 시작 시 자동 실행", Checked = IsStartupEnabled(), AutoSize = true };

        _exceptionList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended };
        _exceptionList.Items.AddRange(settings.ExceptionWords.Cast<object>().ToArray());

        _appGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle,
        };
        _appGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "앱", ReadOnly = true, FillWeight = 50 });
        _appGrid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "자동 모드 끔", FillWeight = 25 });
        _appGrid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "단축키 끔", FillWeight = 25 });
        foreach (var (exe, rule) in settings.Apps.OrderBy(kv => kv.Key)) _appGrid.Rows.Add(exe, !rule.AutoMode, !rule.Hotkey);

        var hotkeys = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        hotkeys.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        hotkeys.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hotkeys.Controls.Add(Label("변환 단축키"), 0, 0);
        hotkeys.Controls.Add(_convertBox, 1, 0);
        hotkeys.Controls.Add(Label("자동 모드 켜고 끄기"), 0, 1);
        hotkeys.Controls.Add(_toggleBox, 1, 1);
        hotkeys.Controls.Add(_autoCheck, 1, 2);
        hotkeys.Controls.Add(_startupCheck, 1, 3);

        var removeWord = new Button { Text = "선택 삭제", AutoSize = true, Dock = DockStyle.Right };
        removeWord.Click += (_, _) =>
        {
            foreach (var item in _exceptionList.SelectedItems.Cast<object>().ToList()) _exceptionList.Items.Remove(item);
        };
        var removeApp = new Button { Text = "선택 삭제", AutoSize = true, Dock = DockStyle.Right };
        removeApp.Click += (_, _) =>
        {
            foreach (DataGridViewRow row in _appGrid.SelectedRows) _appGrid.Rows.Remove(row);
        };

        var save = new Button { Text = "저장", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => Save();
        AcceptButton = save;
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        body.Controls.Add(Group("자동 모드 예외 단어 (같은 단어의 자동 변환을 2번 되돌리면 추가)", _exceptionList, removeWord), 0, 0);
        body.Controls.Add(Group("앱별 설정 (트레이 메뉴에서 추가)", _appGrid, removeApp), 0, 1);

        Shown += (_, _) => _appGrid.ClearSelection();

        Controls.Add(body);
        Controls.Add(Group("단축키", hotkeys, null, autoSize: true));
        Controls.Add(buttons);
    }

    static Label Label(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 3) };

    static GroupBox Group(string title, Control content, Control? footer, bool autoSize = false)
    {
        var box = new GroupBox { Text = title, Dock = autoSize ? DockStyle.Top : DockStyle.Fill, Padding = new Padding(8), AutoSize = autoSize };
        box.Controls.Add(content);
        if (footer is not null)
        {
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(0, 4, 0, 0) };
            bar.Controls.Add(footer);
            box.Controls.Add(bar);
        }
        return box;
    }

    void Save()
    {
        if (_convertBox.Value == _toggleBox.Value)
        {
            MessageBox.Show(this, "두 단축키가 같아요. 서로 다르게 정해 주세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }
        _settings.ConvertHotkey = _convertBox.Value.ToString();
        _settings.AutoToggleHotkey = _toggleBox.Value.ToString();
        _settings.AutoMode = _autoCheck.Checked;
        _settings.ExceptionWords = new SortedSet<string>(_exceptionList.Items.Cast<string>(), StringComparer.Ordinal);
        _settings.Apps.Clear();
        foreach (DataGridViewRow row in _appGrid.Rows)
        {
            var rule = new AppRule { AutoMode = !(bool)(row.Cells[1].Value ?? false), Hotkey = !(bool)(row.Cells[2].Value ?? false) };
            if (!rule.AutoMode || !rule.Hotkey) _settings.Apps[(string)row.Cells[0].Value!] = rule;
        }
        _settings.Save();
        SetStartup(_startupCheck.Checked);
    }

    static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    static void SetStartup(bool isEnabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (isEnabled) key.SetValue(RunValue, $"\"{Application.ExecutablePath}\"");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    /// <summary>Read-only box that records the next key combination pressed while it has focus.</summary>
    sealed class HotkeyBox : TextBox
    {
        public HotkeyBox(Hotkey value)
        {
            Value = value;
            ReadOnly = true;
            BackColor = SystemColors.Window;
            Cursor = Cursors.Hand;
            Dock = DockStyle.Fill;
            Margin = new Padding(3, 4, 3, 4);
            Text = value.ToString();
            GotFocus += (_, _) => Text = "원하는 조합을 누르세요…";
            LostFocus += (_, _) => Text = Value.ToString();
        }

        public Hotkey Value { get; private set; }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return true;
            bool win = (Control.ModifierKeys & Keys.LWin) != 0 || Native.IsDown(0x5B) || Native.IsDown(0x5C);
            var candidate = new Hotkey(key, (keyData & Keys.Control) != 0, (keyData & Keys.Alt) != 0, (keyData & Keys.Shift) != 0, win);
            // Plain keys would fire while typing; require Ctrl, Alt or Win (Shift alone is just a capital letter).
            if (candidate.Ctrl || candidate.Alt || candidate.Win)
            {
                Value = candidate;
                Text = Value.ToString();
            }
            return true;
        }
    }
}

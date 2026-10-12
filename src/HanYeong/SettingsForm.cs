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
    readonly CheckBox _contextCheck;
    readonly CheckBox _idCheck;
    readonly ListBox _exceptionList;
    readonly DataGridView _appGrid;

    public SettingsForm(Settings settings)
    {
        _settings = settings;
        // Sizes below are in 96-DPI units; WinForms scales them to the monitor (125%, 150%, ...) on load.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "한영 변환기 설정";
        Font = new Font("Malgun Gothic", 9F);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = MinimizeBox = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(580, 640);
        MinimumSize = new Size(500, 540);
        Padding = new Padding(12);

        _convertBox = new HotkeyBox(Hotkey.Parse(settings.ConvertHotkey, Hotkey.DefaultConvert));
        _toggleBox = new HotkeyBox(Hotkey.Parse(settings.AutoToggleHotkey, Hotkey.DefaultAutoToggle));
        _autoCheck = new CheckBox { Text = "자동 모드 켜기", Checked = settings.AutoMode, AutoSize = true };
        _startupCheck = new CheckBox { Text = "Windows 시작 시 자동 실행", Checked = IsStartupEnabled(), AutoSize = true };
        _contextCheck = new CheckBox { Text = "문장 맥락으로 짧은 단어도 바꾸기", Checked = settings.SentenceContext, AutoSize = true };
        new ToolTip { AutoPopDelay = 15000 }.SetToolTip(_contextCheck,
            "같은 문장에서 이미 자동으로 바뀐 단어가 있으면\nto(새), so(내), a, I처럼 애매하거나 짧은 단어도 같은 방향으로 바꿉니다.\n문장 끝(. ? ! Enter)이나 그대로 둔 단어가 나오면 끝납니다.");

        _idCheck = new CheckBox { Text = "로그인 칸 아이디 보호", Checked = settings.ProtectIds, AutoSize = true };
        new ToolTip { AutoPopDelay = 15000 }.SetToolTip(_idCheck,
            "빈 칸에 단어 하나만 치고 Enter를 누를 때, 바뀔 결과가 사람 이름처럼 보이면(rlaalswn → 김민주)\n" +
            "아이디로 보고 바꾸지 않습니다. 꺼 두면 다른 단어처럼 바꾸고, 잘못 바뀌면 Ctrl+Space로 되돌릴 수 있습니다.");

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

        var hotkeys = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        hotkeys.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        hotkeys.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hotkeys.Controls.Add(Label("변환 단축키"), 0, 0);
        hotkeys.Controls.Add(_convertBox, 1, 0);
        hotkeys.Controls.Add(Label("자동 모드 켜고 끄기"), 0, 1);
        hotkeys.Controls.Add(_toggleBox, 1, 1);
        hotkeys.Controls.Add(_autoCheck, 1, 2);
        hotkeys.Controls.Add(_contextCheck, 1, 3);
        hotkeys.Controls.Add(_idCheck, 1, 4);
        hotkeys.Controls.Add(_startupCheck, 1, 5);

        var newWord = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "예: rk, and", Margin = new Padding(0, 3, 6, 0) };
        var addWord = new Button { Text = "추가", AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
        addWord.Click += (_, _) =>
        {
            string word = newWord.Text.Trim();
            if (word.Length > 0 && !_exceptionList.Items.Contains(word)) _exceptionList.Items.Add(word);
            newWord.Clear();
            newWord.Focus();
        };
        var removeWord = new Button { Text = "선택 삭제", AutoSize = true, Margin = Padding.Empty };
        removeWord.Click += (_, _) =>
        {
            foreach (var item in _exceptionList.SelectedItems.Cast<object>().ToList()) _exceptionList.Items.Remove(item);
        };
        var wordBar = Bar(newWord, addWord, removeWord);

        // Running apps with a window, so an app can be picked without the tray menu; typing a name also works.
        var appPicker = new ComboBox { Dock = DockStyle.Fill, Margin = new Padding(0, 3, 6, 0) };
        appPicker.Items.AddRange(RunningApps().Cast<object>().ToArray());
        appPicker.Text = "실행 중인 앱 선택 또는 입력 (예: Code.exe)";
        appPicker.ForeColor = SystemColors.GrayText;
        appPicker.Enter += (_, _) =>
        {
            if (appPicker.ForeColor == SystemColors.GrayText) appPicker.Text = "";
            appPicker.ForeColor = SystemColors.WindowText;
        };
        var addApp = new Button { Text = "추가", AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
        addApp.Click += (_, _) =>
        {
            string exe = appPicker.Text.Trim();
            if (exe.Length == 0 || appPicker.ForeColor == SystemColors.GrayText) return;
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
            bool exists = _appGrid.Rows.Cast<DataGridViewRow>().Any(r => string.Equals((string)r.Cells[0].Value!, exe, StringComparison.OrdinalIgnoreCase));
            // New apps start with both off — that is why one adds an app here.
            if (!exists) _appGrid.Rows.Add(exe, true, true);
            appPicker.Text = "";
        };
        var removeApp = new Button { Text = "선택 삭제", AutoSize = true, Margin = Padding.Empty };
        removeApp.Click += (_, _) =>
        {
            foreach (DataGridViewRow row in _appGrid.SelectedRows) _appGrid.Rows.Remove(row);
        };
        var appBar = Bar(appPicker, addApp, removeApp);

        var save = new Button { Text = "저장", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => Save();
        AcceptButton = save;
        CancelButton = cancel;
        // Enter in the word box adds the word instead of saving the dialog.
        newWord.Enter += (_, _) => AcceptButton = addWord;
        newWord.Leave += (_, _) => AcceptButton = save;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);

        // Everything sizes from its content; only the two lists share the leftover height.
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(Group("단축키", hotkeys, null), 0, 0);
        root.Controls.Add(Group("자동 모드가 바꾸지 않을 단어 (친 그대로 rk, 또는 바뀔 결과 and)", _exceptionList, wordBar), 0, 1);
        root.Controls.Add(Group("앱별 설정 (체크한 기능을 그 앱에서 끔)", _appGrid, appBar), 0, 2);
        root.Controls.Add(buttons, 0, 3);
        Controls.Add(root);

        Shown += (_, _) => _appGrid.ClearSelection();
        ResumeLayout(false);
        PerformLayout();
    }

    static Label Label(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 3) };

    /// <summary>An input that stretches, followed by buttons.</summary>
    static TableLayoutPanel Bar(Control input, params Button[] buttons)
    {
        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 + buttons.Length, RowCount = 1, Margin = new Padding(0, 6, 0, 0) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.Controls.Add(input, 0, 0);
        for (int i = 0; i < buttons.Length; i++)
        {
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.Controls.Add(buttons[i], i + 1, 0);
        }
        return bar;
    }

    /// <summary>A titled box: the content fills it, an optional footer row sits below.</summary>
    static GroupBox Group(string title, Control content, Control? footer)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = footer is null ? 1 : 2, AutoSize = footer is null };
        layout.RowStyles.Add(footer is null ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Percent, 100));
        content.Dock = DockStyle.Fill;
        layout.Controls.Add(content, 0, 0);
        if (footer is not null)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(footer, 0, 1);
        }
        var box = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8), AutoSize = footer is null, Margin = new Padding(0, 0, 0, 8) };
        box.Controls.Add(layout);
        return box;
    }

    static IEnumerable<string> RunningApps()
    {
        int self = Environment.ProcessId;
        return System.Diagnostics.Process.GetProcesses()
            .Where(p => p.Id != self && p.MainWindowHandle != IntPtr.Zero)
            .Select(p => p.ProcessName + ".exe")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
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
        _settings.SentenceContext = _contextCheck.Checked;
        _settings.ProtectIds = _idCheck.Checked;
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
        if (isEnabled) key.SetValue(RunValue, $"\"{Application.ExecutablePath}\" {Program.StartupArg}");
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

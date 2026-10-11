using HanYeong.Core;
using static HanYeong.Native;

namespace HanYeong;

/// <summary>
/// Tracks what is typed since the last reset point, converts it on the hotkey and, in auto mode,
/// fixes words typed in the wrong mode when Space is pressed.
/// Typing is tracked inside the hook callback itself so the record and any replacement we inject
/// stay in order with the user's keystrokes.
/// </summary>
sealed class Controller : IDisposable
{
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    const int VK_BACK = 0x08, VK_RETURN = 0x0D, VK_CAPITAL = 0x14;

    record Conversion(string Before, Layout BeforeLayout, string After, Layout AfterLayout)
    {
        public Conversion Reversed => new(After, AfterLayout, Before, BeforeLayout);
    }

    readonly GlobalHooks _hooks;
    readonly SynchronizationContext _ui;
    readonly Settings _settings;
    readonly FocusTracker _focus;
    readonly AutoDetector _detector;
    readonly System.Windows.Forms.Timer _modifierReleaseTimer = new() { Interval = 30 };

    const ushort VK_UNASSIGNED = 0xE8;

    Hotkey _convertKey;
    Hotkey _toggleKey;
    int _swallowedKeyUp;
    // A hotkey was used while Alt/Win were held, so their release is not a "lone tap".
    bool _isModifierUsedByHotkey;
    int _pendingConverts;
    bool _isBusy;
    volatile bool _isDictionaryReady;

    TypedSegment? _segment;
    IntPtr _segmentWindow;
    // Set right after a conversion; pressing the hotkey again before typing anything reverts it.
    Conversion? _last;
    // Set right after an auto conversion; an immediate Backspace undoes it.
    Conversion? _autoUndo;

    public event Action<bool>? AutoModeChanged;

    public Controller(Settings settings, FocusTracker focus)
    {
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Needs the UI thread.");
        _settings = settings;
        _focus = focus;
        _detector = new AutoDetector(word => _settings.ExceptionWords.Contains(word));
        _hooks = new GlobalHooks { KeyPressed = OnHookKey, MouseClicked = Reset };
        _modifierReleaseTimer.Tick += (_, _) => RunPendingWhenModifiersReleased();
        ApplySettings();

        // Loading the word tables takes a moment; the auto mode stays idle until they are in memory.
        Task.Run(() =>
        {
            _ = WordFrequency.English.Value;
            _ = WordFrequency.Korean.Value;
            _ = CommonSyllables.Count;
            _isDictionaryReady = true;
            Log.Write("dictionaries loaded");
        });
    }

    public Hotkey ConvertKey => _convertKey;
    public Hotkey ToggleKey => _toggleKey;

    /// <summary>Re-reads hotkeys after the settings window saved.</summary>
    public void ApplySettings()
    {
        _convertKey = Hotkey.Parse(_settings.ConvertHotkey, Hotkey.DefaultConvert);
        _toggleKey = Hotkey.Parse(_settings.AutoToggleHotkey, Hotkey.DefaultAutoToggle);
    }

    public bool AutoMode
    {
        get => _settings.AutoMode;
        set
        {
            _settings.AutoMode = value;
            _settings.Save();
            _autoUndo = null;
            AutoModeChanged?.Invoke(value);
        }
    }

    static bool IsModifier(int vk) => vk is >= 0xA0 and <= 0xA5 or VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN;
    static bool IsAltOrWin(int vk) => vk is 0xA4 or 0xA5 or VK_MENU or VK_LWIN or VK_RWIN;

    // Keys that never change the text: Caps/Num/Scroll Lock, F1–F24, media and browser keys.
    static bool IsIgnorable(int vk) => vk is VK_CAPITAL or 0x90 or 0x91 or >= 0x70 and <= 0x87 or >= 0xA6 and <= 0xB7 or 0xFF;

    AppRule? CurrentRule => _settings.Apps.GetValueOrDefault(_focus.CurrentExe);

    /// <returns>true to swallow the key.</returns>
    bool OnHookKey(KeyEvent e)
    {
        // Leave our own settings window alone, so the hotkey boxes can record any combination.
        if (_focus.IsOwnAppForeground)
        {
            Reset();
            return false;
        }

        bool ctrl = IsDown(VK_CONTROL), alt = IsDown(VK_MENU), shift = IsDown(VK_SHIFT);
        bool win = IsDown(VK_LWIN) || IsDown(VK_RWIN);

        if (e.IsDown)
        {
            // The auto-mode toggle works everywhere; conversion respects per-app rules and password fields.
            bool isToggle = _toggleKey.Matches(e.Vk, ctrl, alt, shift, win);
            bool isConvert = !isToggle && _convertKey.Matches(e.Vk, ctrl, alt, shift, win)
                && CurrentRule?.Hotkey != false && !_focus.IsPasswordFocused;
            if (isToggle || isConvert)
            {
                _swallowedKeyUp = e.Vk;
                if (alt || win)
                {
                    _isModifierUsedByHotkey = true;
                    // An unassigned key between Alt/Win down and up stops the menu bar / Start menu from opening.
                    new InputSender().Key(VK_UNASSIGNED).Send();
                }
                if (isToggle) _ui.Post(_ => AutoMode = !AutoMode, null);
                else QueueConvert();
                return true;
            }
        }
        else if (e.Vk == _swallowedKeyUp)
        {
            _swallowedKeyUp = 0;
            return true;
        }

        if (!e.IsDown)
        {
            // A lone Alt/Win tap moves focus to a menu, and right Alt is the 한/영 key on many setups.
            if (IsAltOrWin(e.Vk))
            {
                if (!_isModifierUsedByHotkey) Reset();
                if (!IsDown(VK_MENU) && !IsDown(VK_LWIN) && !IsDown(VK_RWIN)) _isModifierUsedByHotkey = false;
            }
            return false;
        }

        if (_focus.IsPasswordFocused)
        {
            // Never keep what is typed into a password field.
            Reset();
            return false;
        }

        bool isCombo = (ctrl || alt || win) && !IsModifier(e.Vk);
        bool capsLock = (GetKeyState(VK_CAPITAL) & 1) != 0;
        return OnKeyDown(e.Vk, shift, isCombo, capsLock);
    }

    bool OnKeyDown(int vk, bool shift, bool isCombo, bool capsLock)
    {
        IntPtr fg = GetForegroundWindow();
        if (_segment is not null && fg != _segmentWindow) Reset();
        if (IsModifier(vk) || IsIgnorable(vk)) return false;

        var autoUndo = _autoUndo;
        _autoUndo = null;

        if (isCombo)
        {
            Reset();
            return false;
        }
        if (vk == VK_BACK)
        {
            if (autoUndo is not null)
            {
                UndoAuto(autoUndo);
                return true;
            }
            _segment?.Backspace();
            _last = null;
            return false;
        }
        if (KeyMap.ToChar(vk, shift, capsLock) is { } c)
        {
            if (WordEnds.Contains(c) && TryAutoConvert(c)) return true;
            if (_segment is null)
            {
                var layout = Ime.GetLayout();
                if (layout is null) Log.Write("IME mode unknown, assuming English");
                _segment = new TypedSegment(layout ?? Layout.English);
                _segmentWindow = fg;
            }
            _segment.Type(c, capsLock);
            _last = null;
            return false;
        }
        if (vk == VK_RETURN && TryAutoConvert(null))
        {
            Reset();
            return true;
        }
        Reset();
        return false;
    }

    // Characters that end a word and trigger the auto mode (Enter does too).
    static readonly char[] WordEnds = [' ', '.', ',', '?', '!', ';', ':'];

    /// <summary>
    /// When a word ends (<paramref name="end"/> = Space/punctuation, or null for Enter), replaces it if it was typed
    /// in the wrong mode. The ending key is swallowed and retyped after the replacement.
    /// </summary>
    bool TryAutoConvert(char? end)
    {
        if (!AutoMode || !_isDictionaryReady || _segment is null || CurrentRule?.AutoMode == false) return false;

        string text = _segment.Text;
        string word = text[(text.LastIndexOfAny(WordEnds) + 1)..];
        if (word.Length == 0 || _detector.Check(word, _segment.Layout) is not { } replacement) return false;
        // The user may list either side: what they typed (rk) or what it would become (and).
        if (_settings.ExceptionWords.Contains(replacement)) return false;
        if (_segment.Layout == Layout.Korean) replacement = _segment.RestoreCase(replacement);

        string ending = end?.ToString() ?? "";
        var conversion = new Conversion(word + ending, _segment.Layout, replacement + ending, _segment.Other);
        // The ending key has not reached the app, so only the word is deleted.
        var input = new InputSender().Key(InputSender.VK_BACK, _segment.BackspacesFor(word.Length)).Text(conversion.After);
        if (end is null) input.Key(InputSender.VK_RETURN);
        Send(input);
        _segment = new TypedSegment(conversion.BeforeLayout);
        _last = conversion;
        // Undoing across a line break is not offered; Enter is a reset point anyway.
        _autoUndo = end is null ? null : conversion;
        Log.Write($"auto {conversion.BeforeLayout}->{conversion.AfterLayout} len={word.Length}->{replacement.Length} end={(end is null ? "enter" : "char")}");
        return true;
    }

    void UndoAuto(Conversion conversion)
    {
        // The IME mode was never changed, so it is still the one the word was typed in.
        Replace(conversion.After.Length, conversion.Before, conversion.BeforeLayout);
        _last = conversion.Reversed;
        Log.Write($"auto undone len={conversion.Before.Length}");
    }

    void QueueConvert()
    {
        // Wait for the modifiers to be released, otherwise our Backspaces would arrive as Ctrl+Backspace.
        _pendingConverts++;
        _modifierReleaseTimer.Start();
    }

    void RunPendingWhenModifiersReleased()
    {
        if (IsDown(VK_CONTROL) || IsDown(VK_MENU) || IsDown(VK_SHIFT) || IsDown(VK_LWIN) || IsDown(VK_RWIN)) return;
        _modifierReleaseTimer.Stop();
        int count = _pendingConverts;
        _pendingConverts = 0;
        // Pressing the hotkey twice while holding the modifiers cancels out.
        if (count % 2 == 1) _ = ConvertAsync();
    }

    async Task ConvertAsync()
    {
        if (_isBusy) return;
        _isBusy = true;
        try
        {
            _autoUndo = null;
            IntPtr fg = GetForegroundWindow();
            if (_segment is not null && fg == _segmentWindow)
            {
                if (_last is { } last)
                {
                    Replace(last.After.Length, last.Before, _segment.Layout);
                    _last = last.Reversed;
                    Log.Write($"revert {last.AfterLayout}->{last.BeforeLayout} len={last.After.Length}");
                    return;
                }
                if (!_segment.IsEmpty)
                {
                    var conversion = new Conversion(_segment.Text, _segment.Layout, _segment.Converted, _segment.Other);
                    Replace(_segment.BackspacesFor(conversion.Before.Length), conversion.After, _segment.Layout);
                    _last = conversion;
                    Log.Write($"convert {conversion.BeforeLayout}->{conversion.AfterLayout} len={conversion.Before.Length}->{conversion.After.Length}");
                    return;
                }
            }
            // Nothing typed since the last click/arrow key: the user probably selected text.
            await ConvertSelectionAsync(fg);
        }
        catch (Exception ex)
        {
            Log.Write($"convert failed: {ex}");
        }
        finally
        {
            _isBusy = false;
        }
    }

    async Task ConvertSelectionAsync(IntPtr window)
    {
        string? text = await SelectionReader.ReadAsync();
        if (string.IsNullOrEmpty(text)) return;

        Layout from = LayoutConverter.Detect(text);
        Layout to = from == Layout.Korean ? Layout.English : Layout.Korean;
        string converted = from == Layout.Korean ? LayoutConverter.ToEnglish(text) : LayoutConverter.ToKorean(text);

        // Typing over the selection replaces it.
        Send(new InputSender().Text(converted));
        _segment = new TypedSegment(Ime.GetLayout() ?? Layout.English);
        _segmentWindow = window;
        _last = new Conversion(text, from, converted, to);
        Log.Write($"convert selection {from}->{to} len={text.Length}");
    }

    /// <summary>
    /// Erases with Backspace and types <paramref name="newText"/> as Unicode, which the IME passes through.
    /// The 한/영 mode is never touched: toggling it to commit a syllable is unreliable in TSF apps such as
    /// the Windows 11 Notepad, so the composing syllable is erased jamo by jamo instead (see BackspacesFor).
    /// </summary>
    void Replace(int backspaces, string newText, Layout imeLayout)
    {
        Send(new InputSender().Key(InputSender.VK_BACK, backspaces).Text(newText));
        // Further typing happens in the unchanged mode, so tracking starts over from here.
        _segment = new TypedSegment(imeLayout);
    }

    static void Send(InputSender input)
    {
        if (!input.Send()) Log.Write("SendInput was blocked (elevated window?)");
    }

    void Reset()
    {
        _segment = null;
        _last = null;
        _autoUndo = null;
    }

    public void Dispose()
    {
        _hooks.Dispose();
        _modifierReleaseTimer.Dispose();
    }
}

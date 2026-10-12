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
    const ushort VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;
    const int VK_BACK = 0x08, VK_RETURN = 0x0D, VK_CAPITAL = 0x14, VK_LEFT = 0x25, VK_DOWN = 0x28;

    record Conversion(string Before, Layout BeforeLayout, string After, Layout AfterLayout)
    {
        public Conversion Reversed => new(After, AfterLayout, Before, BeforeLayout);
    }

    readonly GlobalHooks? _hooks;
    readonly SynchronizationContext _ui;
    readonly Settings _settings;
    readonly IDesktop _desktop;
    readonly AutoDetector _detector;
    readonly AutoDetector _contextDetector;
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
    // Set right after a conversion (also an automatic one); pressing the hotkey before typing anything reverts it.
    Conversion? _last;
    // Mode of the words the auto mode fixed earlier in this sentence: the user is typing in the wrong mode.
    Layout? _contextFrom;
    // A word was already finished since the last click/reset; the tracked segment itself restarts after conversions.
    bool _hasEarlierWord;

    public event Action<bool>? AutoModeChanged;

    public Controller(Settings settings, IDesktop desktop, bool hooks = true)
    {
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Needs the UI thread.");
        _settings = settings;
        _desktop = desktop;
        _detector = new AutoDetector(word => _settings.ExceptionWords.Contains(word));
        _contextDetector = new AutoDetector(word => _settings.ExceptionWords.Contains(word), AutoDetectorOptions.InContext);
        // The headless tests drive OnHookKey/OnMouseClick themselves.
        _hooks = hooks ? new GlobalHooks { KeyPressed = OnHookKey, MouseClicked = OnMouseClick } : null;
        _modifierReleaseTimer.Tick += (_, _) => RunPendingWhenModifiersReleased();
        ApplySettings();

        // Loading the word tables takes a moment; the auto mode stays idle until they are in memory.
        Ready = Task.Run(() =>
        {
            _ = WordFrequency.English.Value;
            _ = WordFrequency.Korean.Value;
            _ = NameList.Default.Value;
            _ = CommonSyllables.Count;
            _isDictionaryReady = true;
            Log.Write("dictionaries loaded");
        });
    }

    /// <summary>Completes when the word tables are loaded and the auto mode works.</summary>
    internal Task Ready { get; }

    internal void OnMouseClick() => Reset();

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
            AutoModeChanged?.Invoke(value);
        }
    }

    static bool IsModifier(int vk) => vk is >= 0xA0 and <= 0xA5 or VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN;
    static bool IsAltOrWin(int vk) => vk is 0xA4 or 0xA5 or VK_MENU or VK_LWIN or VK_RWIN;

    // Keys that never change the text: Caps/Num/Scroll Lock, F1–F24, media and browser keys.
    static bool IsIgnorable(int vk) => vk is VK_CAPITAL or 0x90 or 0x91 or >= 0x70 and <= 0x87 or >= 0xA6 and <= 0xB7 or 0xFF;

    AppRule? CurrentRule => _settings.Apps.GetValueOrDefault(_desktop.CurrentExe);

    /// <returns>true to swallow the key.</returns>
    internal bool OnHookKey(KeyEvent e)
    {
        // Leave our own settings window alone, so the hotkey boxes can record any combination.
        if (_desktop.IsOwnAppForeground)
        {
            Reset();
            return false;
        }

        bool ctrl = _desktop.IsDown(VK_CONTROL), alt = _desktop.IsDown(VK_MENU), shift = _desktop.IsDown(VK_SHIFT);
        bool win = _desktop.IsDown(VK_LWIN) || _desktop.IsDown(VK_RWIN);

        if (e.IsDown)
        {
            // The auto-mode toggle works everywhere; conversion respects per-app rules and password fields.
            bool isToggle = _toggleKey.Matches(e.Vk, ctrl, alt, shift, win);
            bool isConvert = !isToggle && _convertKey.Matches(e.Vk, ctrl, alt, shift, win)
                && CurrentRule?.Hotkey != false && !_desktop.IsPasswordFocused;
            if (isToggle || isConvert)
            {
                _swallowedKeyUp = e.Vk;
                if (alt || win)
                {
                    _isModifierUsedByHotkey = true;
                    // An unassigned key between Alt/Win down and up stops the menu bar / Start menu from opening.
                    Send(new InputSender().Key(VK_UNASSIGNED));
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
                if (!_desktop.IsDown(VK_MENU) && !_desktop.IsDown(VK_LWIN) && !_desktop.IsDown(VK_RWIN)) _isModifierUsedByHotkey = false;
            }
            return false;
        }

        if (_desktop.IsPasswordFocused)
        {
            // Never keep what is typed into a password field.
            Reset();
            return false;
        }

        bool isCombo = (ctrl || alt || win) && !IsModifier(e.Vk);
        bool capsLock = _desktop.IsCapsLockOn;
        return OnKeyDown(e.Vk, shift, isCombo, capsLock);
    }

    bool OnKeyDown(int vk, bool shift, bool isCombo, bool capsLock)
    {
        IntPtr fg = _desktop.ForegroundWindow;
        if (_segment is not null && fg != _segmentWindow) Reset();
        if (IsModifier(vk) || IsIgnorable(vk)) return false;

        if (isCombo)
        {
            Reset();
            return false;
        }
        if (vk == VK_BACK)
        {
            _segment?.Backspace();
            _last = null;
            return false;
        }
        if (KeyMap.ToChar(vk, shift, capsLock) is { } c)
        {
            if (WordEnds.Contains(c))
            {
                bool isConverted = TryAutoConvert(c);
                _hasEarlierWord = true;
                if (isConverted) return true;
            }
            if (_segment is null)
            {
                var layout = _desktop.ImeLayout;
                if (layout is null) Log.Write("IME mode unknown, assuming English");
                _segment = new TypedSegment(layout ?? Layout.English);
                _segmentWindow = fg;
            }
            _segment.Type(c, capsLock);
            _last = null;
            return false;
        }
        // Enter and the arrow keys end the word too; they are replayed after the replacement and reset tracking.
        if ((vk == VK_RETURN || vk is >= VK_LEFT and <= VK_DOWN) && TryAutoConvert(null, (ushort)vk))
        {
            Reset();
            return true;
        }
        Reset();
        return false;
    }

    // Characters that end a word and trigger the auto mode (Enter and arrow keys do too).
    static readonly char[] WordEnds = [' ', '.', ',', '?', '!', ';', ':'];

    /// <summary>
    /// When a word ends, replaces it if it was typed in the wrong mode. The ending key is swallowed and replayed
    /// after the replacement: <paramref name="end"/> for Space/punctuation, <paramref name="endKey"/> for Enter/arrows.
    /// </summary>
    bool TryAutoConvert(char? end, ushort endKey = 0)
    {
        if (!AutoMode || !_isDictionaryReady || _segment is null || CurrentRule?.AutoMode == false) return false;

        string text = _segment.Text;
        string word = text[(text.LastIndexOfAny(WordEnds) + 1)..];
        if (word.Length == 0) return false;
        bool isInContext = _settings.SentenceContext && _contextFrom == _segment.Layout;
        var detector = isInContext ? _contextDetector : _detector;
        // The keys carry the typed capitals, which Korean mode does not show (Hello → ㅗ디ㅣㅐ).
        string keys = _segment.Layout == Layout.English ? word : _segment.RestoreCase(LayoutConverter.ToEnglish(word));
        string? replacement = detector.Suggest(word, keys, _segment.Layout);
        if (replacement is not null && _segment.Layout == Layout.Korean) replacement = _segment.RestoreCase(replacement);
        // The user may list either side: what they typed (rk) or what it would become (and).
        if (replacement is null || _settings.ExceptionWords.Contains(replacement))
        {
            // A word left as typed means the user writes in this mode on purpose ("hello 친구"): context ends.
            _contextFrom = null;
            return false;
        }
        // A single word and then Enter, with nothing else typed since the last click, may be an ID (rlaalswn is
        // someone's ID, not 김민주): it only converts into a common word, as one-word chat messages are (ekdma → 다음).
        if (endKey == VK_RETURN && !_hasEarlierWord && word.Length == text.Length && !IsCommonOnItsOwn(replacement, _segment.Layout))
            return false;

        // Looking back: a lone jamo right before, left alone because it was ambiguous on its own (ㅑ is I), is
        // fixed along with this word: ㅑ ㅈ무ㅅ → I want. Proper Korean before stays (내 want may be meant).
        string before = text[..^word.Length];
        if (_segment.Layout == Layout.Korean && before.EndsWith(' ') && LeadInFix(before[..^1], word) is { } leadIn)
        {
            word = before[(before[..^1].LastIndexOfAny(WordEnds) + 1)..] + word;
            replacement = leadIn + " " + replacement;
        }

        string ending = end?.ToString() ?? "";
        var conversion = new Conversion(word + ending, _segment.Layout, replacement + ending, _segment.Other);
        // Shift may still be held from a capital (PTE then Space): our Backspaces would arrive as Shift+Backspace,
        // which some apps handle differently while a syllable is being composed. Released for the replacement
        // and pressed again afterwards, as the user still holds it.
        var heldShifts = new[] { VK_LSHIFT, VK_RSHIFT }.Where(k => _desktop.IsDown(k)).ToArray();
        var input = new InputSender();
        foreach (var k in heldShifts) input.Up(k);
        // The ending key has not reached the app, so only the word is deleted.
        input.Key(InputSender.VK_BACK, _segment.BackspacesFor(word.Length)).Text(conversion.After);
        if (endKey != 0) input.Key(endKey);
        foreach (var k in heldShifts) input.Down(k);
        Send(input);
        _segment = new TypedSegment(conversion.BeforeLayout);
        _last = conversion;
        _contextFrom = end is '.' or '?' or '!' ? null : conversion.BeforeLayout;
        Log.Write($"auto {conversion.BeforeLayout}->{conversion.AfterLayout} len={word.Length}->{replacement.Length} end=0x{(endKey == 0 ? end!.Value : endKey):X2}{(heldShifts.Length > 0 ? " shift" : "")}");
        return true;
    }

    /// <summary>
    /// The English for the word just before <paramref name="following"/> (<paramref name="textBefore"/> ends with
    /// it) when it is broken Hangul that reads as English in a sentence; otherwise null.
    /// </summary>
    string? LeadInFix(string textBefore, string following)
    {
        string previous = textBefore[(textBefore.LastIndexOfAny(WordEnds) + 1)..];
        if (previous.Length == 0 || !previous.All(Jamo.IsHangul) || AutoDetector.IsWellFormed(previous)) return null;
        // Capitals as typed: the keys of "previous following" are the tail of the segment.
        string keys = _segment!.RestoreCase(LayoutConverter.ToEnglish(previous + " " + following))[..LayoutConverter.ToEnglish(previous).Length];
        string? english = _contextDetector.Suggest(previous, keys, Layout.Korean);
        if (english is null || _settings.ExceptionWords.Contains(english) || _settings.ExceptionWords.Contains(previous)) return null;
        return string.Equals(english, keys, StringComparison.OrdinalIgnoreCase) ? keys : english;
    }

    // Chat messages of one word are everyday words (다음 5.6, 네 6.7, 수고하세요 3.7); IDs made of names are rarer
    // (김민주 2.9, 이지은 3.4).
    static bool IsCommonOnItsOwn(string replacement, Layout typedIn) =>
        (typedIn == Layout.English ? WordFrequency.Korean.Value : WordFrequency.English.Value).Score(replacement) >= 3.5f;

    void QueueConvert()
    {
        // Wait for the modifiers to be released, otherwise our Backspaces would arrive as Ctrl+Backspace.
        _pendingConverts++;
        _modifierReleaseTimer.Start();
    }

    internal void RunPendingWhenModifiersReleased()
    {
        if (_desktop.IsDown(VK_CONTROL) || _desktop.IsDown(VK_MENU) || _desktop.IsDown(VK_SHIFT) || _desktop.IsDown(VK_LWIN) || _desktop.IsDown(VK_RWIN)) return;
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
            IntPtr fg = _desktop.ForegroundWindow;
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
        string? text = await _desktop.ReadSelectionAsync();
        if (string.IsNullOrEmpty(text)) return;

        Layout from = LayoutConverter.Detect(text);
        Layout to = from == Layout.Korean ? Layout.English : Layout.Korean;
        string converted = from == Layout.Korean ? LayoutConverter.ToEnglish(text) : LayoutConverter.ToKorean(text);

        // Typing over the selection replaces it.
        Send(new InputSender().Text(converted));
        _segment = new TypedSegment(_desktop.ImeLayout ?? Layout.English);
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

    void Send(InputSender input)
    {
        if (!_desktop.Send(input.Strokes)) Log.Write("SendInput was blocked (elevated window?)");
    }

    void Reset()
    {
        _segment = null;
        _last = null;
        _contextFrom = null;
        _hasEarlierWord = false;
    }

    public void Dispose()
    {
        _hooks?.Dispose();
        _modifierReleaseTimer.Dispose();
    }
}

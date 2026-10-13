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
    const ushort VK_HANGUL = 0x15, VK_HANJA = 0x19;

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
    readonly AutoDetector _stayingDetector;
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

    // Terminals read input in chunks, and a Backspace or Enter arriving in one chunk with text is taken as part of
    // a paste: Claude Code in cmd dropped the Backspaces and made Enter a new line. There every Backspace goes out
    // alone, then the text, then Enter, a few milliseconds apart.
    static readonly HashSet<string> Terminals = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal.exe", "cmd.exe", "conhost.exe", "OpenConsole.exe", "powershell.exe", "pwsh.exe", "node.exe",
        "claude.exe", "wsl.exe", "bash.exe", "mintty.exe", "alacritty.exe", "wezterm-gui.exe", "ConEmu64.exe", "Tabby.exe",
    };
    const int TerminalGapMs = 4, TerminalEnterGapMs = 15, HanjaModeGapMs = 50;
    // Keys the user typed while a replacement was still going out slowly; they follow it.
    readonly Queue<KeyEvent> _heldKeys = new();

    /// <summary>Completes when no replacement is going out (slowly, in a terminal).</summary>
    internal Task Sending { get; private set; } = Task.CompletedTask;

    TypedSegment? _segment;
    IntPtr _segmentWindow;
    // Set right after a conversion (also an automatic one); pressing the hotkey before typing anything reverts it.
    Conversion? _last;
    // Mode of the words the auto mode fixed earlier in this sentence: the user is typing in the wrong mode.
    Layout? _contextFrom;
    // Mode of a sure word left as typed earlier in this sentence (the, 그리고): the user writes in this mode.
    Layout? _writingIn;
    // The last word changed only because of the sentence context (내 → so after hello); a sure word of the typed
    // mode right after it means it was meant as typed, and it goes back.
    Conversion? _contextOnly;
    // A word was already finished since the last click/reset; the tracked segment itself restarts after conversions.
    bool _hasEarlierWord;
    // The last automatic conversion and the word as typed. Backspace right after it makes it a candidate: typed again
    // the same, the user wanted it as typed (sns became 눈, was erased and typed again), and it stays so in this
    // window, as does a word whose conversion was undone with the hotkey. Erased and typed again once more, it
    // converts again. Nothing is learned beyond the window.
    (Conversion Conversion, string Word, Layout Layout)? _lastAuto;
    (string Word, Layout Layout)? _erasedAuto, _lastKept, _erasedKept;
    readonly HashSet<(IntPtr Window, string Word, Layout Layout)> _keptByUser = [];

    public event Action<bool>? AutoModeChanged;

    public Controller(Settings settings, IDesktop desktop, bool hooks = true)
    {
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Needs the UI thread.");
        _settings = settings;
        _desktop = desktop;
        _detector = new AutoDetector(word => _settings.ExceptionWords.Contains(word));
        _contextDetector = new AutoDetector(word => _settings.ExceptionWords.Contains(word), AutoDetectorOptions.InContext);
        _stayingDetector = new AutoDetector(word => _settings.ExceptionWords.Contains(word), AutoDetectorOptions.Staying);
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
        if (!Sending.IsCompleted)
        {
            _heldKeys.Enqueue(e);
            return true;
        }
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
            if (_last is { } l && _lastAuto is { } auto && ReferenceEquals(l, auto.Conversion)) _erasedAuto = (auto.Word, auto.Layout);
            if (_lastKept is { } kept) _erasedKept = kept;
            _last = null;
            _lastKept = null;
            return false;
        }
        if (vk == VK_HANJA && TryHanja()) return true;
        if (KeyMap.ToChar(vk, shift, capsLock) is { } c)
        {
            _lastKept = null;
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
        if (AutoDetector.IsInAddress(text[..^word.Length], word)) return false;
        bool isInContext = _settings.SentenceContext && _contextFrom == _segment.Layout;
        // The keys carry the typed capitals, which Korean mode does not show (Hello → ㅗ디ㅣㅐ).
        string keys = _segment.Layout == Layout.English ? word : _segment.RestoreCase(LayoutConverter.ToEnglish(word));
        // After a sure word left as typed, a short word (en, di) keeps the mode unless the other reading is far more likely.
        // Not when it reads as a particle and is rarer English: std rk is std 가, the particle typed after a space
        // (rk 3.1); Leonardo di stays (di 4.7).
        bool isStaying = !isInContext && _settings.SentenceContext && _writingIn == _segment.Layout && keys.Length <= 3
            && !(_segment.Layout == Layout.English && AutoDetector.IsParticleAfterSpace(keys));
        var detector = isInContext ? _contextDetector : isStaying ? _stayingDetector : _detector;
        string? replacement = detector.Suggest(word, keys, _segment.Layout);
        // Erased right after it was converted and typed again the same: meant as typed. Erased right after it was
        // kept so and typed again: the conversion was wanted after all.
        var typed = (_segmentWindow, word, _segment.Layout);
        if (replacement is not null && _erasedAuto == (word, _segment.Layout) && _keptByUser.Add(typed))
            Log.Write($"kept as typed after erasing its conversion len={word.Length}");
        else if (_erasedKept == (word, _segment.Layout) && _keptByUser.Remove(typed))
            Log.Write($"converts again after erasing it len={word.Length}");
        _erasedAuto = _erasedKept = null;
        if (replacement is not null && _keptByUser.Contains(typed))
        {
            replacement = null;
            _lastKept = (word, _segment.Layout);
        }
        // In an English sentence typed in Korean mode, a number with an English ending is English too (75소 → 75th).
        if (replacement is null && isInContext && _segment.Layout == Layout.Korean && NumberWithEnglishEnding(word) is { } number)
            replacement = number;
        if (replacement is not null && _segment.Layout == Layout.Korean) replacement = _segment.RestoreCase(replacement);
        // The user may list either side: what they typed (rk) or what it would become (and).
        if (replacement is null || _settings.ExceptionWords.Contains(replacement))
        {
            // A word left as typed means the user writes in this mode on purpose ("hello 친구"): context ends.
            _contextFrom = null;
            // A sure word of this mode, one that even the sentence context leaves (the, not so), says so for the
            // following short words too.
            bool isSure = _detector.IsKnownWord(word, _segment.Layout) && _contextDetector.Suggest(word, keys, _segment.Layout) is null;
            _writingIn = isSure && end is not ('.' or '?' or '!') ? _segment.Layout : null;
            // hello 내 친구: 내 became so for the sentence of English, but 친구 is Korean typed as meant, so 내 was too.
            if (isSure && _contextOnly is { } changed && word == text)
            {
                string ending0 = end?.ToString() ?? "";
                SendReplacement(_segment.BackspacesFor(word.Length) + changed.After.Length, changed.Before + word + ending0, endKey);
                _segment = new TypedSegment(changed.BeforeLayout);
                _contextOnly = null;
                _last = null;
                Log.Write($"context undone len={changed.After.Length}->{changed.Before.Length}");
                return true;
            }
            _contextOnly = null;
            return false;
        }
        // With the ID protection on (Settings.ProtectIds), a single word and then Enter, with nothing else typed
        // since the last click, may be an ID (rlaalswn is someone's ID, not 김민주): it converts unless it reads as a name.
        if (_settings.ProtectIds && endKey == VK_RETURN && !_hasEarlierWord && word.Length == text.Length && !IsMessageOnItsOwn(replacement, _segment.Layout))
            return false;

        // Looking back: the word right before, left alone because it was ambiguous on its own, is fixed along with
        // this one: ㅑ ㅈ무ㅅ → I want, 무 메ㅔㅣㄷ → an apple (see LeadInFix).
        string before = text[..^word.Length];
        string typedWord = word;
        string? leadIn = !before.EndsWith(' ') ? null
            : _segment.Layout == Layout.Korean ? LeadInFix(before[..^1], word)
            : KoreanLeadIn(before[..^1]);
        if (leadIn is not null)
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
        // The ending key has not reached the app, so only the word is deleted.
        SendReplacement(_segment.BackspacesFor(word.Length), conversion.After, endKey, heldShifts);
        _segment = new TypedSegment(conversion.BeforeLayout);
        _last = conversion;
        _lastAuto = (conversion, typedWord, conversion.BeforeLayout);
        // Changed only because of the context, as one word: it may go back when the next word says so.
        _contextOnly = isInContext && leadIn is null && _detector.Suggest(word, keys, conversion.BeforeLayout) is null ? conversion : null;
        _contextFrom = end is '.' or '?' or '!' ? null : conversion.BeforeLayout;
        _writingIn = null;
        Log.Write($"auto {conversion.BeforeLayout}->{conversion.AfterLayout} len={word.Length}->{replacement.Length} end=0x{(endKey == 0 ? end!.Value : endKey):X2}{(heldShifts.Length > 0 ? " shift" : "")}");
        return true;
    }

    // 20소 → 20th, 1980ㄴ → 1980s: a number and an English ending that reads as Hangul.
    static string? NumberWithEnglishEnding(string word)
    {
        int digits = word.TakeWhile(char.IsAsciiDigit).Count();
        if (digits == 0 || digits == word.Length || !word[digits..].All(Jamo.IsHangul)) return null;
        string ending = LayoutConverter.ToEnglish(word[digits..]);
        return AutoDetector.IsEnglishNumberSuffix(ending) ? word[..digits] + ending : null;
    }

    /// <summary>
    /// The English for the word just before <paramref name="following"/> (<paramref name="textBefore"/> ends with
    /// it) when it reads as English in a sentence: broken Hangul (ㅑ → I), a Korean word whose English is far
    /// more common (무 → an), or a number with an English ending (20소 → 20th); otherwise null.
    /// </summary>

    string? LeadInFix(string textBefore, string following)
    {
        string previous = textBefore[(textBefore.LastIndexOfAny(WordEnds) + 1)..];
        // A number with an English ending that reads as Hangul: 20소 ㅇㄱㄷ므 → 20th dream (alone, 20소 may be Korean).
        if (previous.Length > 0 && char.IsAsciiDigit(previous[0])) return NumberWithEnglishEnding(previous);
        if (previous.Length == 0 || !previous.All(Jamo.IsHangul)) return null;
        // Capitals as typed: the keys of "previous following" are the tail of the segment.
        string keys = _segment!.RestoreCase(LayoutConverter.ToEnglish(previous + " " + following))[..LayoutConverter.ToEnglish(previous).Length];
        string? english = AutoDetector.IsWellFormed(previous)
            // Proper Korean only when the English is far more common: 무 → an (6.6 vs 4.4), 새 → to; not 내 (so,
            // both 6.7) or 해 (go), which may be Korean before an English word on purpose.
            ? WordFrequency.English.Value.Score(keys) is var en && en >= 6.0f && en - WordFrequency.Korean.Value.Score(previous) >= 1.5f
                ? LayoutConverter.ToEnglish(previous) : null
            : _contextDetector.Suggest(previous, keys, Layout.Korean);
        if (english is null || _settings.ExceptionWords.Contains(english) || _settings.ExceptionWords.Contains(previous)) return null;
        return string.Equals(english, keys, StringComparison.OrdinalIgnoreCase) ? keys : english;
    }

    /// <summary>
    /// The Hanja key after Korean typed in English mode (rh → 고): the word is typed again in Korean mode so the
    /// IME composes it itself, then the Hanja key goes to the IME, which offers Hanja for the syllable being
    /// composed (故, 高, 古 ...). The 한/영 mode is switched here only because Hanja means Korean is wanted.
    /// </summary>
    bool TryHanja()
    {
        // An app where the hotkey is off keeps its own Hanja key.
        if (CurrentRule?.Hotkey == false) return false;
        if (_segment is not { Layout: Layout.English } || _segment.IsEmpty) return false;
        string text = _segment.Text;
        string word = text[(text.LastIndexOfAny(WordEnds) + 1)..];
        if (word.Length == 0 || !word.All(char.IsAsciiLetter) || !AutoDetector.IsWellFormed(LayoutConverter.ToKorean(word))) return false;

        var toKorean = new InputSender().Key(InputSender.VK_BACK, word.Length).Key(VK_HANGUL);
        var retype = new InputSender();
        foreach (char c in word)
        {
            // Shift only where it was typed: ㅃ ㅉ ㄸ ㄲ ㅆ ㅒ ㅖ.
            bool shift = char.IsAsciiLetterUpper(c);
            if (shift) retype.Down(VK_LSHIFT);
            retype.Key((ushort)char.ToUpperInvariant(c));
            if (shift) retype.Up(VK_LSHIFT);
        }
        retype.Key(VK_HANJA);
        // The IME takes a moment to switch to Korean (TSF apps such as Notepad); a key sent right after the 한/영
        // key can get lost, so the word follows a little later.
        Sending = SendStepsAsync([(toKorean, HanjaModeGapMs), (retype, 0)], []);
        // What the user picks from the Hanja list is not known here.
        Reset();
        Log.Write($"hanja len={word.Length}");
        return true;
    }

    /// <summary>
    /// English mode: the Korean for the word just before (<paramref name="textBefore"/> ends with it) when a
    /// sentence of Korean reads it so: so wlq → 내 집 (so and 내 are equally common). Clearly English stays (the, my).
    /// The first word typed, read as a Korean word that needs a word after it (새, 두, 그), follows too, however common
    /// the English: to anfrjs → 새 물건 (in go to gkrry, to stays).
    /// </summary>
    string? KoreanLeadIn(string textBefore)
    {
        string previous = textBefore[(textBefore.LastIndexOfAny(WordEnds) + 1)..];
        if (previous.Length == 0 || !previous.All(char.IsAsciiLetter) || _settings.ExceptionWords.Contains(previous)) return null;
        string? korean = _contextDetector.Suggest(previous, previous, Layout.English)
            ?? (previous == textBefore && previous.All(char.IsAsciiLetterLower) && LayoutConverter.ToKorean(previous) is var reading
                && Determiners.Contains(reading) ? reading : null);
        return korean is not null && korean.All(Jamo.IsSyllable) && !_settings.ExceptionWords.Contains(korean) ? korean : null;
    }

    // Korean words that stand only before another word: 새 책, 두 개, 그 사람.
    static readonly HashSet<string> Determiners = ["새", "헌", "첫", "옛", "온", "각", "몇", "매", "그", "이", "저", "두", "세", "네", "내", "제"];

    // Chat messages of one word are everyday words (다음 5.6, 네 6.7, 수고하세요 3.7), or Korean of five syllables
    // and more, or a word + ending (진행상황에, 확인했어). IDs made of names are short and rarer (김민주 2.9, 이지은 3.4).
    // In Korean mode the protection is not needed: an English ID typed there (ㅡㅑㅜ내ㅐ) is meant as English.
    // In English mode it is for IDs made of names (rlaalswn = 김민주): anything that does not look like a name
    // converts (퀴즈, 노트북), and so does a common word that does (정말).
    bool IsMessageOnItsOwn(string replacement, Layout typedIn) =>
        typedIn == Layout.Korean || !LooksLikeKoreanName(replacement)
        || WordFrequency.Korean.Value.Score(replacement) >= 3.5f || _detector.IsWordWithEnding(replacement);

    // The most common surnames; a Korean name is two to four syllables after one of them.
    const string Surnames = "김이박최정강조윤장임한오서신권황안송류유전홍고문양손배백허남심노하곽성차주우구민나진지엄채원천방공현함변염여추도소석선설마길연위표명기반왕금옥육인맹제모탁국어은편용예경봉사부";

    static bool LooksLikeKoreanName(string text) =>
        text.Length is >= 2 and <= 4 && text.All(Jamo.IsSyllable) && Surnames.Contains(text[0]);

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
                    // An automatic conversion undone: that word stays as typed from now on.
                    if (_lastAuto is { } auto && ReferenceEquals(last, auto.Conversion)) _keptByUser.Add((fg, auto.Word, auto.Layout));
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
        SendReplacement(backspaces, newText);
        // Further typing happens in the unchanged mode, so tracking starts over from here.
        _segment = new TypedSegment(imeLayout);
    }

    /// <summary>
    /// Backspaces, then the text, then the ending key; with any held Shift released around the text. The ending key
    /// goes with Shift as pressed: Shift+Enter is a new line in chat apps, a plain Enter would send the message.
    /// </summary>
    void SendReplacement(int backspaces, string text, ushort endKey = 0, ushort[]? heldShifts = null)
    {
        heldShifts ??= [];
        var steps = new List<(InputSender Input, int GapAfterMs)>();
        if (!Terminals.Contains(_desktop.CurrentExe))
        {
            var all = new InputSender().Key(InputSender.VK_BACK, backspaces).Text(text);
            if (endKey != 0) AddEndKey(all);
            steps.Add((all, 0));
        }
        else
        {
            for (int i = 0; i < backspaces; i++) steps.Add((new InputSender().Key(InputSender.VK_BACK), TerminalGapMs));
            steps.Add((new InputSender().Text(text), TerminalEnterGapMs));
            if (endKey != 0) steps.Add((AddEndKey(new InputSender()), 0));
        }

        InputSender AddEndKey(InputSender input)
        {
            foreach (var k in heldShifts) input.Down(k);
            input.Key(endKey);
            foreach (var k in heldShifts) input.Up(k);
            return input;
        }
        Sending = SendStepsAsync(steps, heldShifts);
    }

    async Task SendStepsAsync(List<(InputSender Input, int GapAfterMs)> steps, ushort[] heldShifts)
    {
        var release = new InputSender();
        foreach (var k in heldShifts) release.Up(k);
        Send(release);
        bool hasGaps = steps.Count > 1;
        // 1 ms timer resolution only while the gaps run, so a 4 ms wait is not 15.6 ms.
        if (hasGaps) Native.timeBeginPeriod(1);
        try
        {
            for (int i = 0; i < steps.Count; i++)
            {
                Send(steps[i].Input);
                if (i < steps.Count - 1 && steps[i].GapAfterMs > 0) await Task.Delay(steps[i].GapAfterMs);
            }
        }
        finally
        {
            if (hasGaps) Native.timeEndPeriod(1);
        }
        var press = new InputSender();
        foreach (var k in heldShifts) press.Down(k);
        Send(press);
        if (_heldKeys.Count > 0)
        {
            var keys = _heldKeys.ToList();
            _heldKeys.Clear();
            // Marked as done first, so the replayed keys go through the hook as typed.
            _ui.Post(_ => _desktop.Replay(keys), null);
        }
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
        _writingIn = null;
        _contextOnly = null;
        _hasEarlierWord = false;
    }

    public void Dispose()
    {
        _hooks?.Dispose();
        _modifierReleaseTimer.Dispose();
    }
}

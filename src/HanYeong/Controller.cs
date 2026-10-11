using HanYeong.Core;
using static HanYeong.Native;

namespace HanYeong;

/// <summary>
/// Tracks what is typed since the last reset point and converts it on the hotkey.
/// Hook callbacks only decide whether to swallow a key; everything else is posted to the UI thread.
/// </summary>
sealed class Controller : IDisposable
{
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    const int VK_SPACE = 0x20, VK_BACK = 0x08, VK_CAPITAL = 0x14;

    record Conversion(string Before, Layout BeforeLayout, string After, Layout AfterLayout);

    readonly GlobalHooks _hooks;
    readonly SynchronizationContext _ui;
    readonly System.Windows.Forms.Timer _ctrlReleaseTimer = new() { Interval = 30 };

    bool _isSpaceSwallowed;
    int _pendingConverts;
    bool _isBusy;

    TypedSegment? _segment;
    IntPtr _segmentWindow;
    // Set right after a conversion; pressing the hotkey again before typing anything reverts it.
    Conversion? _last;

    public event Action? AutoModeToggleRequested;

    public Controller()
    {
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Needs the UI thread.");
        _hooks = new GlobalHooks { KeyPressed = OnHookKey, MouseClicked = () => _ui.Post(_ => Reset("click"), null) };
        _ctrlReleaseTimer.Tick += (_, _) => RunPendingWhenCtrlReleased();
    }

    static bool IsModifier(int vk) => vk is >= 0xA0 and <= 0xA5 or VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN;
    static bool IsAltOrWin(int vk) => vk is 0xA4 or 0xA5 or VK_MENU or VK_LWIN or VK_RWIN;

    bool OnHookKey(KeyEvent e)
    {
        bool ctrl = IsDown(VK_CONTROL), alt = IsDown(VK_MENU), shift = IsDown(VK_SHIFT);
        bool win = IsDown(VK_LWIN) || IsDown(VK_RWIN);

        if (e.Vk == VK_SPACE)
        {
            if (e.IsDown && ctrl && !shift && !win)
            {
                _isSpaceSwallowed = true;
                if (alt) _ui.Post(_ => AutoModeToggleRequested?.Invoke(), null);
                else _ui.Post(_ => QueueConvert(), null);
                return true;
            }
            if (!e.IsDown && _isSpaceSwallowed)
            {
                _isSpaceSwallowed = false;
                return true;
            }
        }

        if (e.IsDown)
        {
            bool isCombo = (ctrl || alt || win) && !IsModifier(e.Vk);
            bool capsLock = (GetKeyState(VK_CAPITAL) & 1) != 0;
            _ui.Post(_ => OnKeyDown(e.Vk, shift, isCombo, capsLock), null);
        }
        else if (IsAltOrWin(e.Vk))
        {
            // A lone Alt/Win tap moves focus to a menu, and right Alt is the 한/영 key on many setups.
            _ui.Post(_ => Reset("alt/win"), null);
        }
        return false;
    }

    void OnKeyDown(int vk, bool shift, bool isCombo, bool capsLock)
    {
        IntPtr fg = GetForegroundWindow();
        if (_segment is not null && fg != _segmentWindow) Reset("window changed");

        if (IsModifier(vk) || IsIgnorable(vk)) return;
        if (isCombo)
        {
            Reset("shortcut");
            return;
        }
        if (vk == VK_BACK)
        {
            _segment?.Backspace();
            _last = null;
            return;
        }
        if (KeyMap.ToChar(vk, shift, capsLock) is { } c)
        {
            if (_segment is null)
            {
                var layout = Ime.GetLayout();
                if (layout is null) Log.Write("IME mode unknown, assuming English");
                _segment = new TypedSegment(layout ?? Layout.English);
                _segmentWindow = fg;
            }
            _segment.Type(c, capsLock);
            _last = null;
            return;
        }
        Reset($"key 0x{vk:X2}");
    }

    // Keys that never change the text: Caps/Num/Scroll Lock, F1–F24, media and browser keys.
    static bool IsIgnorable(int vk) => vk is VK_CAPITAL or 0x90 or 0x91 or >= 0x70 and <= 0x87 or >= 0xA6 and <= 0xB7 or 0xFF;

    void QueueConvert()
    {
        // Wait for Ctrl to be released, otherwise our Backspaces would arrive as Ctrl+Backspace.
        _pendingConverts++;
        _ctrlReleaseTimer.Start();
    }

    void RunPendingWhenCtrlReleased()
    {
        if (IsDown(VK_CONTROL)) return;
        _ctrlReleaseTimer.Stop();
        int count = _pendingConverts;
        _pendingConverts = 0;
        // Pressing the hotkey twice while holding Ctrl cancels out.
        if (count % 2 == 1) _ = ConvertAsync();
    }

    async Task ConvertAsync()
    {
        if (_isBusy) return;
        _isBusy = true;
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (_segment is not null && fg == _segmentWindow)
            {
                if (_last is { } last)
                {
                    Replace(last.After, last.AfterLayout, last.Before, last.BeforeLayout);
                    _last = new Conversion(last.After, last.AfterLayout, last.Before, last.BeforeLayout);
                    Log.Write($"revert {last.AfterLayout}->{last.BeforeLayout} len={last.After.Length}");
                    return;
                }
                if (!_segment.IsEmpty)
                {
                    var conversion = new Conversion(_segment.Text, _segment.Layout, _segment.Converted, _segment.Other);
                    Replace(conversion.Before, conversion.BeforeLayout, conversion.After, conversion.AfterLayout);
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

        var input = new InputSender();
        if (Ime.GetLayout() == Layout.Korean) input.Key(InputSender.VK_HANGUL);
        // Typing over the selection replaces it.
        input.Text(converted);
        if (to == Layout.Korean) input.Key(InputSender.VK_HANGUL);
        if (!input.Send()) Log.Write("SendInput was blocked (elevated window?)");

        _segment = new TypedSegment(to, converted);
        _segmentWindow = window;
        _last = new Conversion(text, from, converted, to);
        Log.Write($"convert selection {from}->{to} len={text.Length}");
    }

    void Replace(string oldText, Layout oldLayout, string newText, Layout newLayout)
    {
        var input = new InputSender();
        // Toggling to English first also commits the syllable the IME is still composing.
        if ((Ime.GetLayout() ?? oldLayout) == Layout.Korean) input.Key(InputSender.VK_HANGUL);
        input.Key(InputSender.VK_BACK, oldText.Length).Text(newText);
        if (newLayout == Layout.Korean) input.Key(InputSender.VK_HANGUL);
        if (!input.Send()) Log.Write("SendInput was blocked (elevated window?)");
        _segment = new TypedSegment(newLayout, newText);
    }

    void Reset(string reason)
    {
        _segment = null;
        _last = null;
    }

    public void Dispose()
    {
        _hooks.Dispose();
        _ctrlReleaseTimer.Dispose();
    }
}

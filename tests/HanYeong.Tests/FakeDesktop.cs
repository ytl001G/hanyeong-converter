using HanYeong.Core;

namespace HanYeong.Tests;

/// <summary>
/// A text box with the Korean IME, as the app sees it: keys the controller lets through and the keystrokes it
/// injects both land here. The IME is <see cref="HangulComposer"/>, which follows the Microsoft IME (jamo-by-jamo
/// Backspace while composing, a Unicode character ends the composition).
/// </summary>
sealed class FakeTextBox
{
    HangulComposer _left = new();   // everything left of the caret, the syllable being composed included
    string _right = "";

    public string Text => _left.Text + _right;
    public string Composing => _left.Composing;

    public void Type(char key, Layout mode)
    {
        if (mode == Layout.Korean && char.IsAsciiLetter(key)) _left.Type(key);
        else _left.AppendCommitted(key.ToString());
    }

    public void Backspace() => _left.Backspace();

    public void Insert(char c) => _left.AppendCommitted(c.ToString());

    public void Left()
    {
        _left.Commit();
        string text = _left.Text;
        if (text.Length == 0) return;
        _left.Backspace();
        _right = text[^1] + _right;
    }

    public void Right()
    {
        _left.Commit();
        if (_right.Length == 0) return;
        _left.AppendCommitted(_right[0].ToString());
        _right = _right[1..];
    }
}

/// <summary>Windows for the controller: one focused text box, a keyboard and the IME mode.</summary>
sealed class FakeDesktop : IDesktop
{
    public const int VK_BACK = 0x08, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_SPACE = 0x20;
    public const int VK_HANGUL = 0x15, VK_HANJA = 0x19;
    public const int VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2;

    readonly HashSet<int> _down = new();

    public FakeTextBox Box { get; } = new();
    public Layout Mode { get; set; } = Layout.English;
    public string? Selection { get; set; }
    public List<Keystroke> Injected { get; } = new();
    /// <summary>For each Enter that reached the box, whether Shift was down (a new line in chat apps, not send).</summary>
    public List<bool> EntersWithShift { get; } = new();
    /// <summary>The syllable being composed each time the Hanja key reached the IME.</summary>
    public List<string> HanjaRequests { get; } = new();

    public IntPtr ForegroundWindow { get; set; } = 1;
    public string Exe { get; set; } = "test.exe";
    /// <summary>Every Send call, as the app made it.</summary>
    public List<List<Keystroke>> Batches { get; } = new();
    public bool IsDown(int vk) => _down.Contains(vk)
        || (vk == VK_SHIFT && (_down.Contains(VK_LSHIFT) || _down.Contains(VK_RSHIFT)))
        || (vk == VK_CONTROL && _down.Contains(VK_LCONTROL));
    public bool IsCapsLockOn => false;
    public Layout? ImeLayout => Mode;
    public string CurrentExe => Exe;
    public bool IsPasswordFocused => false;
    public bool IsOwnAppForeground => false;
    public Task<string?> ReadSelectionAsync() => Task.FromResult(Selection);

    public void Replay(IReadOnlyList<KeyEvent> keys)
    {
        foreach (var k in keys)
        {
            if (k.IsDown) Down(k.Vk); else Up(k.Vk);
        }
    }

    public bool Send(IReadOnlyList<Keystroke> strokes)
    {
        if (strokes.Count > 0) Batches.Add(strokes.ToList());
        foreach (var k in strokes)
        {
            Injected.Add(k);
            if (k.Vk is VK_LSHIFT or VK_RSHIFT)
            {
                if (k.IsUp) _down.Remove(k.Vk); else _down.Add(k.Vk);
                continue;
            }
            if (k.IsUp) continue;
            if (k.Vk == 0) Box.Insert(k.Char);
            else Apply(k.Vk);
        }
        return true;
    }

    Controller? _controller;
    public void Attach(Controller controller) => _controller = controller;

    /// <summary>A physical key goes through the hook first; the box only gets it when it is not swallowed.</summary>
    public void Down(int vk)
    {
        _down.Add(vk);
        if (!_controller!.OnHookKey(new KeyEvent(vk, true))) Apply(vk);
    }

    public void Up(int vk)
    {
        _down.Remove(vk);
        _controller!.OnHookKey(new KeyEvent(vk, false));
    }

    public void Tap(int vk)
    {
        Down(vk);
        Up(vk);
    }

    /// <summary>Types like a person: Shift for capitals, Enter for '\n'.</summary>
    public void Type(string text)
    {
        foreach (char c in text)
        {
            var (vk, shift) = KeyFor(c);
            if (shift) Down(VK_LSHIFT);
            Tap(vk);
            if (shift) Up(VK_LSHIFT);
        }
    }

    public void Click() => _controller!.OnMouseClick();

    /// <summary>Ctrl+Space, then the modifiers are released and the controller's timer fires.</summary>
    public void Hotkey()
    {
        Down(VK_LCONTROL);
        Tap(VK_SPACE);
        Up(VK_LCONTROL);
        _controller!.RunPendingWhenModifiersReleased();
    }

    void Apply(int vk)
    {
        switch (vk)
        {
            case VK_BACK: Box.Backspace(); break;
            case VK_HANGUL: Mode = Mode == Layout.Korean ? Layout.English : Layout.Korean; break;
            case VK_HANJA: if (Mode == Layout.Korean) HanjaRequests.Add(Box.Composing); break;
            case VK_RETURN: EntersWithShift.Add(IsDown(VK_SHIFT)); Box.Insert('\n'); break;
            case VK_LEFT: Box.Left(); break;
            case VK_RIGHT: Box.Right(); break;
            case VK_SHIFT or VK_CONTROL or VK_LSHIFT or VK_RSHIFT or VK_LCONTROL or 0xE8: break;
            default:
                if (CharFor(vk, IsDown(VK_SHIFT)) is { } c) Box.Type(c, Mode);
                break;
        }
    }

    static (int Vk, bool Shift) KeyFor(char c) => c switch
    {
        ' ' => (VK_SPACE, false),
        '\n' => (VK_RETURN, false),
        '.' => (0xBE, false),
        ',' => (0xBC, false),
        '-' => (0xBD, false),
        '+' => (0xBB, true),
        '(' => ('9', true),
        ')' => ('0', true),
        '$' => ('4', true),
        '\'' => (0xDE, false),
        _ when char.IsAsciiLetter(c) => (char.ToUpperInvariant(c), char.IsAsciiLetterUpper(c)),
        _ when char.IsAsciiDigit(c) => (c, false),
        _ => throw new ArgumentException($"no key for '{c}'"),
    };

    static char? CharFor(int vk, bool shift) => vk switch
    {
        VK_SPACE => ' ',
        0xBE => '.',
        0xBC => ',',
        0xBD => shift ? '_' : '-',
        0xBB => shift ? '+' : '=',
        0xDE => shift ? '"' : '\'',
        >= 'A' and <= 'Z' => shift ? (char)vk : char.ToLowerInvariant((char)vk),
        '9' when shift => '(',
        '0' when shift => ')',
        '4' when shift => '$',
        >= '0' and <= '9' => (char)vk,
        _ => null,
    };
}

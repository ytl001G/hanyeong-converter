using System.Runtime.InteropServices;
using static HanYeong.Native;

namespace HanYeong;

/// <summary>Builds and sends a batch of synthetic keystrokes.</summary>
sealed class InputSender
{
    public const ushort VK_BACK = 0x08;
    public const ushort VK_RETURN = 0x0D;

    // Marks our own events so the hook can tell them apart from other injected input.
    public static readonly IntPtr Signature = 0x48594E47; // "HYNG"

    readonly List<Keystroke> _strokes = new();

    public IReadOnlyList<Keystroke> Strokes => _strokes;

    public InputSender Key(ushort vk, int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            _strokes.Add(new Keystroke(vk, '\0', false));
            _strokes.Add(new Keystroke(vk, '\0', true));
        }
        return this;
    }

    public InputSender Down(ushort vk)
    {
        _strokes.Add(new Keystroke(vk, '\0', false));
        return this;
    }

    public InputSender Up(ushort vk)
    {
        _strokes.Add(new Keystroke(vk, '\0', true));
        return this;
    }

    public InputSender Chord(ushort modifier, ushort vk)
    {
        _strokes.Add(new Keystroke(modifier, '\0', false));
        Key(vk);
        _strokes.Add(new Keystroke(modifier, '\0', true));
        return this;
    }

    /// <summary>Types text directly as Unicode characters, bypassing the IME.</summary>
    public InputSender Text(string text)
    {
        foreach (char c in text)
        {
            // Apps handle a Unicode '\n' inconsistently; Enter is understood everywhere.
            if (c == '\r') continue;
            if (c == '\n')
            {
                Key(VK_RETURN);
                continue;
            }
            _strokes.Add(new Keystroke(0, c, false));
            _strokes.Add(new Keystroke(0, c, true));
        }
        return this;
    }

    public bool Send()
    {
        bool ok = SendNow(_strokes);
        _strokes.Clear();
        return ok;
    }

    public static bool SendNow(IReadOnlyList<Keystroke> strokes)
    {
        if (strokes.Count == 0) return true;
        var array = strokes.Select(k => k.Vk != 0
            ? Keyboard(k.Vk, '\0', k.IsUp ? KEYEVENTF_KEYUP : 0)
            : Keyboard(0, k.Char, KEYEVENTF_UNICODE | (k.IsUp ? KEYEVENTF_KEYUP : 0))).ToArray();
        return SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>()) == array.Length;
    }

    static INPUT Keyboard(ushort vk, char scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = Signature } },
    };
}

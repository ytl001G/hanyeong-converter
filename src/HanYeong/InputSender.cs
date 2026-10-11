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

    readonly List<INPUT> _inputs = new();

    public InputSender Key(ushort vk, int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            _inputs.Add(Keyboard(vk, (char)0, 0));
            _inputs.Add(Keyboard(vk, (char)0, KEYEVENTF_KEYUP));
        }
        return this;
    }

    public InputSender Chord(ushort modifier, ushort vk)
    {
        _inputs.Add(Keyboard(modifier, (char)0, 0));
        Key(vk);
        _inputs.Add(Keyboard(modifier, (char)0, KEYEVENTF_KEYUP));
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
            _inputs.Add(Keyboard(0, c, KEYEVENTF_UNICODE));
            _inputs.Add(Keyboard(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        return this;
    }

    public bool Send()
    {
        if (_inputs.Count == 0) return true;
        var array = _inputs.ToArray();
        _inputs.Clear();
        return SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>()) == array.Length;
    }

    static INPUT Keyboard(ushort vk, char scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = Signature } },
    };
}

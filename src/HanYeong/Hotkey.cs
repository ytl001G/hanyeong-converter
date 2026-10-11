namespace HanYeong;

/// <summary>A key plus modifiers, written like "Ctrl+Alt+Space".</summary>
readonly record struct Hotkey(Keys Key, bool Ctrl, bool Alt, bool Shift, bool Win)
{
    public static readonly Hotkey DefaultConvert = new(Keys.Space, Ctrl: true, Alt: false, Shift: false, Win: false);
    public static readonly Hotkey DefaultAutoToggle = new(Keys.Space, Ctrl: true, Alt: true, Shift: false, Win: false);

    public bool HasModifier => Ctrl || Alt || Shift || Win;

    public bool Matches(int vk, bool ctrl, bool alt, bool shift, bool win) =>
        vk == (int)Key && ctrl == Ctrl && alt == Alt && shift == Shift && win == Win;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    public static Hotkey Parse(string? text, Hotkey fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        bool ctrl = false, alt = false, shift = false, win = false;
        Keys? key = null;
        foreach (string raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win": win = true; break;
                default:
                    if (raw.Length == 1 && char.IsAsciiDigit(raw[0])) key = Keys.D0 + (raw[0] - '0');
                    else if (Enum.TryParse(raw, ignoreCase: true, out Keys parsed)) key = parsed;
                    else return fallback;
                    break;
            }
        }
        return key is { } k ? new Hotkey(k, ctrl, alt, shift, win) : fallback;
    }

    static string KeyName(Keys key) => key is >= Keys.D0 and <= Keys.D9 ? ((int)(key - Keys.D0)).ToString() : key.ToString();
}

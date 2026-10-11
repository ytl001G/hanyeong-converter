namespace HanYeong;

/// <summary>Virtual-key → character on the US/Korean physical layout, as typed in English mode.</summary>
static class KeyMap
{
    const string DigitShift = ")!@#$%^&*(";

    static readonly Dictionary<int, (char Normal, char Shifted)> Oem = new()
    {
        [0x20] = (' ', ' '),
        [0xBA] = (';', ':'), [0xBB] = ('=', '+'), [0xBC] = (',', '<'), [0xBD] = ('-', '_'),
        [0xBE] = ('.', '>'), [0xBF] = ('/', '?'), [0xC0] = ('`', '~'), [0xDB] = ('[', '{'),
        [0xDC] = ('\\', '|'), [0xDD] = (']', '}'), [0xDE] = ('\'', '"'),
        [0x6A] = ('*', '*'), [0x6B] = ('+', '+'), [0x6D] = ('-', '-'), [0x6E] = ('.', '.'), [0x6F] = ('/', '/'),
    };

    public static char? ToChar(int vk, bool shift, bool capsLock)
    {
        if (vk is >= 0x41 and <= 0x5A)
        {
            char c = (char)vk;
            return shift ^ capsLock ? c : char.ToLowerInvariant(c);
        }
        if (vk is >= 0x30 and <= 0x39) return shift ? DigitShift[vk - 0x30] : (char)vk;
        if (vk is >= 0x60 and <= 0x69) return (char)('0' + vk - 0x60);
        if (Oem.TryGetValue(vk, out var pair)) return shift ? pair.Shifted : pair.Normal;
        return null;
    }
}

using System.Text;

namespace HanYeong.Core;

/// <summary>
/// The text typed since the last reset point, tracked as it appears on screen in the
/// input mode it was typed in. In Korean mode the actual keys are kept too, because the
/// screen loses Shift (Shift+H and h both give ㅗ) and conversion should bring back "Hello".
/// </summary>
public sealed class TypedSegment
{
    const string KoreanShiftKeys = "QWERTOP";

    readonly HangulComposer? _korean;
    readonly StringBuilder? _english;
    readonly StringBuilder _keys = new();

    public TypedSegment(Layout layout, string initial = "")
    {
        Layout = layout;
        if (layout == Layout.Korean)
        {
            _korean = new HangulComposer();
            if (initial.Length > 0) _korean.AppendCommitted(initial);
            _keys.Append(LayoutConverter.ToEnglish(initial));
        }
        else
        {
            _english = new StringBuilder(initial);
        }
    }

    public Layout Layout { get; }
    public string Text => _korean?.Text ?? _english!.ToString();
    public bool IsEmpty => Text.Length == 0;

    /// <summary>Layout the text gets converted to.</summary>
    public Layout Other => Layout == Layout.Korean ? Layout.English : Layout.Korean;

    /// <summary>What the text would have been in the other layout, with the original capitals when known.</summary>
    public string Converted => Layout == Layout.Korean ? RestoreCase(LayoutConverter.ToEnglish(Text)) : LayoutConverter.ToKorean(Text);

    /// <summary>
    /// Backspaces needed to delete the last <paramref name="tailLength"/> characters on screen. The syllable the
    /// Korean IME is still composing is erased one jamo per Backspace (닭 → 달 → 다 → ㄷ), so it can take several.
    /// </summary>
    public int BackspacesFor(int tailLength) =>
        _korean is { IsComposing: true } ? tailLength - 1 + _korean.ComposingBackspaces : tailLength;

    /// <summary>
    /// Puts back the capitals that were typed for the tail of the text, e.g. the last word
    /// (<paramref name="english"/> = ToEnglish of that tail). Returns it unchanged if the keys do not line up.
    /// </summary>
    public string RestoreCase(string english)
    {
        if (_korean is null || english.Length > _keys.Length) return english;
        string typed = _keys.ToString(_keys.Length - english.Length, english.Length);
        return CaseInsensitiveForm(typed) == english ? typed : english;
    }

    /// <param name="key">Character the key produces in English mode (Shift and Caps Lock applied).</param>
    /// <param name="capsLock">Caps Lock makes the Korean IME type Latin letters.</param>
    public void Type(char key, bool capsLock = false)
    {
        if (_korean is null)
        {
            _english!.Append(key);
            return;
        }
        if (capsLock && char.IsAsciiLetter(key)) _korean.AppendCommitted(key.ToString());
        else _korean.Type(key);
        _keys.Append(key);
    }

    public void Backspace()
    {
        if (_korean is null)
        {
            if (_english!.Length > 0) _english.Length--;
            return;
        }
        _korean.Backspace();
        // Each jamo maps back to one key, so the keys shrink to the length of the remaining text's keys.
        // If they no longer line up, fall back to what the screen says (capitals are then lost).
        string expected = LayoutConverter.ToEnglish(Text);
        bool isAligned = _keys.Length >= expected.Length && CaseInsensitiveForm(_keys.ToString(0, expected.Length)) == expected;
        if (isAligned) _keys.Length = expected.Length;
        else _keys.Clear().Append(expected);
    }

    // What ToEnglish produces for these keys: Shift only survives on keys with a shifted jamo (ㅃㅉㄸㄲㅆㅒㅖ).
    static string CaseInsensitiveForm(string keys) => string.Create(keys.Length, keys, (span, k) =>
    {
        for (int i = 0; i < k.Length; i++)
            span[i] = char.IsAsciiLetterUpper(k[i]) && !KoreanShiftKeys.Contains(k[i]) ? char.ToLowerInvariant(k[i]) : k[i];
    });
}

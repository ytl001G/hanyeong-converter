using System.Text;

namespace HanYeong.Core;

/// <summary>
/// The text typed since the last reset point, tracked as it appears on screen in the
/// input mode it was typed in.
/// </summary>
public sealed class TypedSegment
{
    readonly HangulComposer? _korean;
    readonly StringBuilder? _english;

    public TypedSegment(Layout layout, string initial = "")
    {
        Layout = layout;
        if (layout == Layout.Korean)
        {
            _korean = new HangulComposer();
            if (initial.Length > 0) _korean.AppendCommitted(initial);
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

    /// <summary>What the text would have been in the other layout.</summary>
    public string Converted => Layout == Layout.Korean ? LayoutConverter.ToEnglish(Text) : LayoutConverter.ToKorean(Text);

    /// <param name="key">Character the key produces in English mode (Shift and Caps Lock applied).</param>
    /// <param name="capsLock">Caps Lock makes the Korean IME type Latin letters.</param>
    public void Type(char key, bool capsLock = false)
    {
        if (_korean is null) _english!.Append(key);
        else if (capsLock && char.IsAsciiLetter(key)) _korean.AppendCommitted(key.ToString());
        else _korean.Type(key);
    }

    public void Backspace()
    {
        if (_korean is not null) _korean.Backspace();
        else if (_english!.Length > 0) _english.Length--;
    }
}

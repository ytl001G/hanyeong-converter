namespace HanYeong.Core;

public enum Layout { English, Korean }

/// <summary>Converts text typed in the wrong input mode.</summary>
public static class LayoutConverter
{
    /// <summary>Text typed in English mode → what Korean mode would have produced (dkssud → 안녕).</summary>
    public static string ToKorean(string text) => HangulComposer.Compose(text);

    /// <summary>Text typed in Korean mode → what English mode would have produced (ㅗ디ㅣㅐ → hello).</summary>
    public static string ToEnglish(string text) => HangulDecomposer.ToKeys(text);

    /// <summary>The layout the text was most likely typed in: Korean if it has more Hangul than Latin letters.</summary>
    public static Layout Detect(string text)
    {
        int hangul = 0, latin = 0;
        foreach (char c in text)
        {
            if (Jamo.IsHangul(c)) hangul++;
            else if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') latin++;
        }
        return hangul > latin ? Layout.Korean : Layout.English;
    }

    /// <summary>Flips the text to the other layout, detecting which one it was typed in.</summary>
    public static string Flip(string text) =>
        Detect(text) == Layout.Korean ? ToEnglish(text) : ToKorean(text);
}

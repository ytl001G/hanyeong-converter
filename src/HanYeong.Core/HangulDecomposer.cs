using System.Text;

namespace HanYeong.Core;

/// <summary>Turns Hangul text back into the dubeolsik keys that would type it (안녕 → dkssud).</summary>
public static class HangulDecomposer
{
    public static string ToKeys(string text)
    {
        var sb = new StringBuilder(text.Length * 3);
        foreach (char c in text)
        {
            if (Jamo.IsSyllable(c))
            {
                var (cho, jung, jong) = Jamo.Decompose(c);
                AppendJamo(sb, cho);
                AppendJamo(sb, jung);
                if (jong != '\0') AppendJamo(sb, jong);
            }
            else if (Jamo.IsJamo(c))
            {
                AppendJamo(sb, c);
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    static void AppendJamo(StringBuilder sb, char jamo)
    {
        if (Dubeolsik.ToKey(jamo) is { } key)
        {
            sb.Append(key);
        }
        else if (Jamo.Split(jamo) is var (first, second))
        {
            sb.Append(Dubeolsik.ToKey(first)).Append(Dubeolsik.ToKey(second));
        }
        else
        {
            // Archaic jamo outside the dubeolsik layout: keep as is.
            sb.Append(jamo);
        }
    }
}

using System.Text;

namespace HanYeong.Core;

/// <summary>
/// The 2,350 syllables of KS X 1001 (EUC-KR). They cover almost all everyday Korean, so a syllable
/// outside the set (뷁, 똠) is a strong hint the text was typed in the wrong mode.
/// </summary>
public static class CommonSyllables
{
    static readonly Lazy<HashSet<char>> Set = new(Build);

    public static int Count => Set.Value.Count;

    public static bool Contains(char syllable) => Set.Value.Contains(syllable);

    static HashSet<char> Build()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var eucKr = Encoding.GetEncoding(51949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var set = new HashSet<char>();
        Span<char> one = stackalloc char[1];
        Span<byte> bytes = stackalloc byte[4];
        for (char c = '가'; c <= '힣'; c++)
        {
            one[0] = c;
            try
            {
                eucKr.GetBytes(one, bytes);
                set.Add(c);
            }
            catch (EncoderFallbackException)
            {
            }
        }
        return set;
    }
}

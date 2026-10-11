namespace HanYeong.Core;

/// <summary>Hangul jamo tables and syllable arithmetic (Unicode 0xAC00 block, compatibility jamo).</summary>
public static class Jamo
{
    public const string Cho = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";
    public const string Jung = "ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ";
    // Index 0 means "no final consonant".
    public const string Jong = "\0ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ";

    const int SyllableBase = 0xAC00;
    const int SyllableCount = 19 * 21 * 28;

    static readonly Dictionary<(char, char), char> VowelPairs = new()
    {
        [('ㅗ', 'ㅏ')] = 'ㅘ', [('ㅗ', 'ㅐ')] = 'ㅙ', [('ㅗ', 'ㅣ')] = 'ㅚ',
        [('ㅜ', 'ㅓ')] = 'ㅝ', [('ㅜ', 'ㅔ')] = 'ㅞ', [('ㅜ', 'ㅣ')] = 'ㅟ',
        [('ㅡ', 'ㅣ')] = 'ㅢ',
    };

    static readonly Dictionary<(char, char), char> ConsonantPairs = new()
    {
        [('ㄱ', 'ㅅ')] = 'ㄳ', [('ㄴ', 'ㅈ')] = 'ㄵ', [('ㄴ', 'ㅎ')] = 'ㄶ',
        [('ㄹ', 'ㄱ')] = 'ㄺ', [('ㄹ', 'ㅁ')] = 'ㄻ', [('ㄹ', 'ㅂ')] = 'ㄼ',
        [('ㄹ', 'ㅅ')] = 'ㄽ', [('ㄹ', 'ㅌ')] = 'ㄾ', [('ㄹ', 'ㅍ')] = 'ㄿ',
        [('ㄹ', 'ㅎ')] = 'ㅀ', [('ㅂ', 'ㅅ')] = 'ㅄ',
    };

    static readonly Dictionary<char, (char, char)> Splits = VowelPairs.Concat(ConsonantPairs)
        .ToDictionary(kv => kv.Value, kv => kv.Key);

    public static bool IsConsonant(char c) => c is >= 'ㄱ' and <= 'ㅎ';
    public static bool IsVowel(char c) => c is >= 'ㅏ' and <= 'ㅣ';
    public static bool IsJamo(char c) => IsConsonant(c) || IsVowel(c);
    public static bool IsSyllable(char c) => c >= SyllableBase && c < SyllableBase + SyllableCount;
    public static bool IsHangul(char c) => IsJamo(c) || IsSyllable(c);

    public static bool CanBeFinal(char consonant) => consonant != '\0' && Jong.IndexOf(consonant) > 0;

    public static char? CombineVowels(char a, char b) => VowelPairs.TryGetValue((a, b), out var v) ? v : null;
    public static char? CombineConsonants(char a, char b) => ConsonantPairs.TryGetValue((a, b), out var c) ? c : null;

    /// <summary>Splits a compound vowel or consonant cluster into its two parts; null for simple jamo.</summary>
    public static (char First, char Second)? Split(char jamo) => Splits.TryGetValue(jamo, out var pair) ? pair : null;

    public static char Compose(char cho, char jung, char jong = '\0') =>
        (char)(SyllableBase + (Cho.IndexOf(cho) * 21 + Jung.IndexOf(jung)) * 28 + (jong == '\0' ? 0 : Jong.IndexOf(jong)));

    public static (char Cho, char Jung, char Jong) Decompose(char syllable)
    {
        int offset = syllable - SyllableBase;
        return (Cho[offset / (21 * 28)], Jung[offset / 28 % 21], Jong[offset % 28]);
    }
}

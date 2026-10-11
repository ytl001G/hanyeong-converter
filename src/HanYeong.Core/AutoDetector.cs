namespace HanYeong.Core;

/// <summary>Thresholds on the Zipf-like scores of <see cref="WordFrequency"/>; defaults are the tuned values.</summary>
public sealed record AutoDetectorOptions
{
    public static readonly AutoDetectorOptions Default = new();

    /// <summary>Score given to well-formed Korean missing from the data (a new eojeol beats an unknown Latin string).</summary>
    public float UnseenKorean { get; init; } = 1.5f;
    /// <summary>English mode: convert when Korean scores at least this much higher.</summary>
    public float EnglishToKoreanMargin { get; init; } = 1.0f;
    /// <summary>Korean mode, well-formed text: convert when English scores at least this much higher.</summary>
    public float KoreanToEnglishMargin { get; init; } = 2.0f;
    /// <summary>Well-formed Korean this common is never treated as a typo (내 vs "so").</summary>
    public float CommonKorean { get; init; } = 3.5f;
    /// <summary>Broken Korean still needs a real English word; rarer entries are mostly corpus noise.</summary>
    public float KnownEnglish { get; init; } = 2.0f;
    /// <summary>Consonant-only/vowel-only input needs very common English (ㅈㄷ → we).</summary>
    public float CommonEnglish { get; init; } = 4.5f;
}

/// <summary>
/// Decides whether a just-finished word was typed in the wrong input mode, by comparing how
/// common it is in each language. Errs on the side of leaving text alone.
/// </summary>
public sealed class AutoDetector
{
    // Shift letters that type double consonants / ㅒㅖ in Korean mode.
    const string KoreanShiftKeys = "QWERTOP";

    // Initial-consonant shorthand used in chat (repeated single jamo like ㅋㅋ are caught separately).
    static readonly HashSet<string> ChatAbbreviations =
    [
        "ㅇㅋ", "ㄱㅅ", "ㅅㄱ", "ㅈㅅ", "ㄴㄴ", "ㅂㅂ", "ㅃㅃ", "ㅎㅇ", "ㄷㄷ", "ㅊㅋ", "ㅁㅊ", "ㅇㅈ", "ㄹㅇ",
        "ㅈㄹ", "ㅅㅂ", "ㅄ", "ㄳ", "ㅇㄷ", "ㄱㄷ", "ㄱㅊ", "ㄱㄴ", "ㅈㄱㄴ", "ㅇㄱㄹㅇ", "ㅂㄷㅂㄷ", "ㄷㄱㄷㄱ",
        "ㅎㄷㄷ", "ㄴㅇㄱ", "ㅁㄹ", "ㅇㅎ", "ㅎㄹ", "ㅋㄷ", "ㅠㅜ", "ㅜㅠ", "ㅃㅇ", "ㄲㅈ", "ㄱㅇㄷ", "ㅅㅌㅊ",
        "ㅎㅌㅊ", "ㅈㅂ", "ㅈㅇ", "ㅇㅇㄴ", "ㄴㅇ", "ㅂㅇ", "ㅊㅊ", "ㅍㅌ", "ㅅㅎ", "ㄷㅊ",
    ];

    readonly Func<string, float> _english;
    readonly Func<string, float> _korean;
    readonly Func<string, bool> _isException;
    readonly AutoDetectorOptions _o;

    public AutoDetector(Func<string, bool>? isException = null, AutoDetectorOptions? options = null)
        : this(w => WordFrequency.English.Value.Score(w), w => WordFrequency.Korean.Value.Score(w), isException, options)
    {
    }

    public AutoDetector(Func<string, float> english, Func<string, float> korean, Func<string, bool>? isException = null, AutoDetectorOptions? options = null)
    {
        _english = english;
        _korean = korean;
        _isException = isException ?? (_ => false);
        _o = options ?? AutoDetectorOptions.Default;
    }

    /// <summary>The replacement for <paramref name="word"/> (as shown on screen), or null to leave it.</summary>
    public string? Check(string word, Layout typedIn)
    {
        if (_isException(word)) return null;
        // Length in keystrokes: 뭉 is three keys, a single letter is too ambiguous.
        int keys = typedIn == Layout.English ? word.Length : LayoutConverter.ToEnglish(word).Length;
        if (keys < 2) return null;
        return typedIn == Layout.English ? CheckEnglish(word) : CheckKorean(word);
    }

    string? CheckEnglish(string word)
    {
        foreach (char c in word)
        {
            if (!char.IsAsciiLetter(c)) return null;
            // Capitals other than Korean shift keys mean a real English word, name or acronym.
            if (char.IsAsciiLetterUpper(c) && !KoreanShiftKeys.Contains(c)) return null;
        }

        string korean = LayoutConverter.ToKorean(word);
        if (!IsWellFormed(korean)) return null;

        float en = _english(word);
        float ko = _korean(korean) is var k && k > 0 ? k : _o.UnseenKorean;
        return ko - en >= _o.EnglishToKoreanMargin ? korean : null;
    }

    string? CheckKorean(string word)
    {
        if (!word.All(Jamo.IsHangul)) return null;
        // ㅋㅋㅋ, ㅠㅠ, ㅇㅋ, ㄱㅅ: chat expressions, not typos.
        bool isRepeatedJamo = word.Length >= 2 && Jamo.IsJamo(word[0]) && word.All(c => c == word[0]);
        if (isRepeatedJamo || ChatAbbreviations.Contains(word)) return null;

        string english = LayoutConverter.ToEnglish(word);
        float en = _english(english);
        if (en < _o.KnownEnglish) return null;
        // Other consonant-only/vowel-only strings could still be chat shorthand; only very common
        // English (ㅈㄷ → we, ㅛㅐㅕ → you) is worth converting.
        if ((word.All(Jamo.IsConsonant) || word.All(Jamo.IsVowel)) && en < _o.CommonEnglish) return null;

        if (!IsWellFormed(word)) return english;

        float ko = _korean(word);
        return ko < _o.CommonKorean && en - ko >= _o.KoreanToEnglishMargin ? english : null;
    }

    /// <summary>Only complete, everyday syllables (no stray jamo, nothing outside KS X 1001).</summary>
    public static bool IsWellFormed(string korean) =>
        korean.Length > 0 && korean.All(c => Jamo.IsSyllable(c) && CommonSyllables.Contains(c));
}

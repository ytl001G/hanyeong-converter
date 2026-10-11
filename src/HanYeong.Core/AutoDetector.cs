namespace HanYeong.Core;

/// <summary>Thresholds on the Zipf-like scores of <see cref="WordFrequency"/>; defaults are the tuned values.</summary>
public sealed record AutoDetectorOptions
{
    public static readonly AutoDetectorOptions Default = new();

    /// <summary>
    /// Sentence context: an earlier word of this sentence was already fixed in the same direction, so the
    /// user is evidently typing in the wrong mode. Ambiguous words (so/내, to/새) and one-key words (a, I) follow.
    /// </summary>
    public static readonly AutoDetectorOptions InContext = Default with
    {
        EnglishToKoreanMargin = 0f,
        KoreanToEnglishMargin = 0f,
        CommonKorean = float.MaxValue,
        MinKeys = 1,
    };

    /// <summary>Score given to well-formed Korean missing from the data (a new eojeol beats an unknown Latin string).</summary>
    public float UnseenKorean { get; init; } = 1.5f;
    /// <summary>English mode: convert when Korean scores at least this much higher.</summary>
    public float EnglishToKoreanMargin { get; init; } = 1.0f;
    /// <summary>Korean mode, well-formed text: convert when English scores at least this much higher.</summary>
    public float KoreanToEnglishMargin { get; init; } = 3.0f;
    /// <summary>Well-formed Korean this common is never treated as a typo (내 vs "so").</summary>
    public float CommonKorean { get; init; } = 3.5f;
    /// <summary>Broken Korean still needs a real English word; rarer entries are mostly corpus noise.</summary>
    public float KnownEnglish { get; init; } = 1.5f;
    /// <summary>Consonant-only/vowel-only input needs very common English (ㅈㄷ → we).</summary>
    public float CommonEnglish { get; init; } = 4.5f;
    /// <summary>English part of a mixed word (shortage + 이다) must be at least this common.</summary>
    public float MixedEnglishPrefix { get; init; } = 3.5f;
    /// <summary>Korean after the English part of a mixed word, when not a particle: a common word (engel + 곡선).</summary>
    public float MixedKoreanRest { get; init; } = 3.5f;
    /// <summary>Shorter words (in keystrokes) are too ambiguous to judge on their own.</summary>
    public int MinKeys { get; init; } = 2;
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
        // The word tables start at two letters; the only one-letter English words are added here.
        _english = w => w.Length == 1 ? (w is "a" or "A" or "i" or "I" ? 7.5f : 0f) : english(w);
        _korean = korean;
        _isException = isException ?? (_ => false);
        _o = options ?? AutoDetectorOptions.Default;
    }

    /// <summary>The replacement for <paramref name="word"/> (as shown on screen), or null to leave it.</summary>
    public string? Check(string word, Layout typedIn)
    {
        if (_isException(word)) return null;
        return word.IndexOfAny(Joiners) >= 0 ? CheckJoined(word, typedIn) : CheckPart(word, typedIn);
    }

    string? CheckPart(string word, Layout typedIn)
    {
        // Length in keystrokes: 뭉 is three keys, a single letter is too ambiguous.
        int keys = KeyCount(word, typedIn);
        if (keys < _o.MinKeys) return null;
        return typedIn == Layout.English ? CheckEnglish(word) : CheckKorean(word);
    }

    static int KeyCount(string word, Layout typedIn) => typedIn == Layout.English ? word.Length : LayoutConverter.ToEnglish(word).Length;

    // Characters that join words without ending them: power-consumption, and/or, don't.
    static readonly char[] Joiners = ['-', '/', '\''];

    /// <summary>
    /// A joined word converts as a whole when some part would convert and no other part is a real word as typed
    /// (ㅔ걏ㄷ-채ㅜ녀ㅡㅔ샤ㅐㅜ → power-consumption; 애ㅜ'ㅅ → don't, where the one-key "t" just follows).
    /// </summary>
    string? CheckJoined(string word, Layout typedIn)
    {
        bool anyConverts = false;
        foreach (string part in word.Split(Joiners))
        {
            if (part.Length == 0) continue;
            if (CheckPart(part, typedIn) is not null) anyConverts = true;
            else if (KeyCount(part, typedIn) >= _o.MinKeys) return null;
        }
        if (!anyConverts) return null;
        // The joiners are the same key in both modes, so the whole word converts as typed.
        return typedIn == Layout.English ? LayoutConverter.ToKorean(word) : LayoutConverter.ToEnglish(word);
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

    // Particles and endings that get attached straight to an English word (shortage이다, update했다, Github에서는).
    static readonly HashSet<string> Particles =
    [
        "이", "가", "은", "는", "을", "를", "에", "에서", "에게", "한테", "께", "의", "도", "만", "와", "과",
        "랑", "이랑", "하고", "로", "으로", "로서", "으로서", "로써", "처럼", "보다", "까지", "부터", "마다", "조차",
        "이나", "나", "이든", "든", "이라도", "라도", "밖에", "뿐", "같은", "같이",
        "이다", "다", "이야", "야", "이에요", "예요", "입니다", "이고", "고", "이며", "며", "이라서", "라서",
        "인데", "인가", "인지", "이라는", "라는", "이라고", "라고", "이면", "면", "이었다", "였다", "이지", "지",
        "이니까", "니까", "이랑은", "임", "인", "일",
        "하다", "한", "하는", "할", "함", "해", "해요", "했다", "했어", "했어요", "했는데", "해서", "하고", "하면",
        "하게", "하지", "합니다", "했습니다", "하려고", "하는데", "됨", "된", "되는", "될", "되다", "됐다", "돼",
        "되면", "되고", "됩니다", "적", "적인", "적으로", "스러운", "스럽다", "들", "님", "씨", "용", "식", "급",
    ];

    /// <summary>
    /// A word that mixes an English word with a Korean particle, typed in one mode: "shortagedlek" in English
    /// mode or "놳ㅁㅎㄷ이다" in Korean mode both become "shortage이다". Only tried when the whole word is not
    /// already a real word. <paramref name="keys"/> are the keys as typed, capitals included.
    /// </summary>
    public string? CheckMixed(string keys, Layout typedIn)
    {
        // A sign in front of a number stays as it is: -6qhek → -6보다.
        if (typedIn == Layout.English && keys.Length > 1 && keys[0] is '-' or '+' && char.IsAsciiDigit(keys[1]))
            return CheckMixed(keys[1..], typedIn) is { } signed ? keys[0] + signed : null;
        // Digits only in English mode (ch4가, mp3파일, 6보다): in Korean mode 초4가 is real Korean ("4th grader").
        if (keys.Length < 3 || !keys.All(c => char.IsAsciiLetter(c) || (typedIn == Layout.English && char.IsAsciiDigit(c)))) return null;
        string display = typedIn == Layout.English ? keys : LayoutConverter.ToKorean(keys);
        if (_isException(display)) return null;
        // Korean mode: Shift on a key without a shifted jamo (S, N, ...) changes nothing in Korean, so it is
        // a sure sign of English (SNS typed there shows as the real word 눈).
        bool isShiftedForEnglish = typedIn == Layout.Korean && HasEnglishOnlyShift(keys);
        // English mode: capitals then lowercase (LArk, UNdp) is an acronym plus a particle, not the word
        // "lark" or "UNDP" that a case-insensitive lookup would find.
        bool isAcronymThenLower = keys.Length >= 3 && char.IsAsciiLetterUpper(keys[0]) && char.IsAsciiLetterUpper(keys[1])
            && keys.Any(char.IsAsciiLetterLower);
        bool isRealWord = typedIn == Layout.English
            ? !isAcronymThenLower && _english(keys) >= _o.KnownEnglish
            : !isShiftedForEnglish && _korean(display) > 0;
        if (isRealWord) return null;

        // Longest English prefix first. A code (mp3, ch4) ends at its last digit, so there the shortest prefix
        // that includes it goes first: mp3 + 파일, not mp3vk + 일.
        int lastDigit = keys.LastIndexOfAny("0123456789".ToCharArray());
        var splits = lastDigit >= 0
            ? Enumerable.Range(lastDigit + 1, keys.Length - lastDigit - 1)
            : Enumerable.Range(2, keys.Length - 2).Reverse();
        foreach (int split in splits)
        {
            string prefix = keys[..split], rest = keys[split..];
            // A particle typed by mistake is plain lowercase letters (Shift only for ㄲㄸㅃㅆㅉㅒㅖ); INGKA is not IN + 함.
            if (!rest.All(char.IsAsciiLetter) || HasEnglishOnlyShift(rest)) continue;
            // Korean mode: English typed there shows up as broken Hangul (놳ㅁㅎㄷ). Proper syllables mean
            // the user was typing Korean on purpose: 추가됩니다 is not "cn" + 가됩니다.
            if (typedIn == Layout.Korean && !HasEnglishOnlyShift(prefix) && IsWellFormed(LayoutConverter.ToKorean(prefix))) continue;
            // An all-caps prefix is a deliberate acronym (SNS를, FTA가) even when the word lists miss it.
            bool isAcronym = prefix.All(char.IsAsciiLetterUpper);
            // Letters with digits (ch4 = chapter 4, mp3, h2o) are codes, never Korean typing; no word list has them.
            // Short only: a long letters+digits run before "rk" is more likely a password-like string.
            bool isCode = prefix.Length <= 6 && prefix.Any(char.IsAsciiDigit) && prefix.Any(char.IsAsciiLetter);
            bool isNumber = prefix.All(char.IsAsciiDigit);
            if (prefix.Any(char.IsAsciiDigit) && !isCode && !isNumber) continue;
            string korean = LayoutConverter.ToKorean(rest);
            // A number takes a particle or a counter (6보다, 3개, 10분, 2024년이); English units and ordinals
            // (3rd, 4th, 10kg) never read as those.
            if (isNumber)
            {
                if (IsParticleChain(korean) || IsCounterChain(korean)) return prefix + korean;
                continue;
            }
            isAcronym |= isCode;
            bool isParticle = IsParticleChain(korean);
            // A rarer English word (giffen, engel, pareto) is enough when the evidence is strong: broken Hangul in
            // Korean mode, or a particle of three or more keys (dms 은, fmf 를, dptj 에서) that English words do not
            // end in. Two-key particles (di 야, ek 다) need a common word, or names split: Namadi is not Nama + 야.
            float needed = typedIn == Layout.Korean || (isParticle && rest.Length >= 3) ? _o.KnownEnglish : _o.MixedEnglishPrefix;
            if (!isAcronym && _english(prefix) < needed) continue;
            // A particle chain (SNS를) or a common word of two or more syllables (engel곡선, LG전자); one syllable
            // alone is too easily the tail of a name (Durango → Duran해).
            bool isCommonWord = korean.Length >= 2 && IsWellFormed(korean) && _korean(korean) >= _o.MixedKoreanRest
                // In English mode the rest must not be English itself: Upwork is not Up + 재가 ("work").
                && !(typedIn == Layout.English && _english(rest) >= _o.KnownEnglish);
            if (isParticle || isCommonWord) return prefix + korean;
        }
        return null;
    }

    // Counters that follow a number (3개, 10분, 2024년).
    static readonly HashSet<string> Counters =
    [
        "개", "명", "번", "분", "초", "시", "일", "월", "년", "원", "살", "층", "장", "권", "등", "점", "회", "배", "차",
        "대", "쪽", "편", "곡", "병", "잔", "벌", "주", "개월", "시간", "주일", "주년", "년생", "학년", "만원", "천원",
        "퍼센트", "프로", "위", "조", "억", "만", "천", "백", "인", "명씩", "개씩", "번째", "째", "줄", "칸", "판", "골",
    ];

    // A counter, optionally followed by particles: 개, 개가, 분에서.
    static bool IsCounterChain(string text)
    {
        for (int length = Math.Min(3, text.Length); length >= 1; length--)
        {
            if (!Counters.Contains(text[..length])) continue;
            string after = text[length..];
            if (after.Length == 0 || IsParticleChain(after)) return true;
        }
        return false;
    }

    static bool HasEnglishOnlyShift(string keys) => keys.Any(c => char.IsAsciiLetterUpper(c) && !KoreanShiftKeys.Contains(c));

    /// <summary>One to three particles/endings in a row: 에서는 = 에서 + 는.</summary>
    public static bool IsParticleChain(string text, int depth = 0)
    {
        if (text.Length == 0) return depth > 0;
        if (depth == 3) return false;
        for (int length = Math.Min(4, text.Length); length >= 1; length--)
        {
            if (Particles.Contains(text[..length]) && IsParticleChain(text[length..], depth + 1)) return true;
        }
        return false;
    }

    /// <summary>Only complete, everyday syllables (no stray jamo, nothing outside KS X 1001).</summary>
    public static bool IsWellFormed(string korean) =>
        korean.Length > 0 && korean.All(c => Jamo.IsSyllable(c) && CommonSyllables.Contains(c));
}

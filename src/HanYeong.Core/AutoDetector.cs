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
        // Korean may even be a little less common than the English reading: go (6.6) → 해 (6.3) after 분석부터.
        EnglishToKoreanMargin = -1f,
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
    readonly Func<string, bool> _isName;
    readonly AutoDetectorOptions _o;

    public AutoDetector(Func<string, bool>? isException = null, AutoDetectorOptions? options = null)
        : this(w => WordFrequency.English.Value.Score(w), w => WordFrequency.Korean.Value.Score(w), isException, options,
            w => NameList.Default.Value.Contains(w))
    {
    }

    public AutoDetector(Func<string, float> english, Func<string, float> korean, Func<string, bool>? isException = null,
        AutoDetectorOptions? options = null, Func<string, bool>? isName = null)
    {
        _isName = isName ?? (_ => false);
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
        if (typedIn == Layout.English && IsKnownName(word)) return null;
        if (!word.All(IsLetter)) return CheckJoined(word, typedIn);
        // All letters, but with an acronym inside (삼성SDS가, 비타민C가): judged in parts like a joined word.
        return CheckPart(word, typedIn)
            ?? (typedIn == Layout.English && word.Any(char.IsAsciiLetterLower)
                && Enumerable.Range(0, word.Length).Any(i => AcronymLength(word, i) > 0)
                ? CheckJoined(word, typedIn) : null);
    }

    // Contractions typed in Korean mode come out as stray jamo around an apostrophe (I'm → ㅑ'ㅡ).
    static readonly HashSet<string> Contractions =
    [
        "i'm", "i've", "i'd", "i'll", "you're", "you've", "you'd", "you'll", "we're", "we've", "we'd", "we'll",
        "they're", "they've", "they'd", "they'll", "he's", "she's", "it's", "that's", "there's", "what's", "who's",
        "here's", "where's", "let's", "isn't", "aren't", "wasn't", "weren't", "don't", "doesn't", "didn't", "can't",
        "couldn't", "won't", "wouldn't", "shouldn't", "haven't", "hasn't", "hadn't", "he'd", "she'd", "he'll", "she'll",
    ];

    /// <summary>
    /// Whether a word that ends right after <paramref name="textBefore"/> (what was typed before it since the last
    /// reset) belongs to an address: www.kt.com, a@b.com, http://x. A word joined to an earlier one by . @ / : is
    /// left alone; an earlier word that was converted starts the text over, so 안녕.하세요 still converts.
    /// </summary>
    public static bool IsInAddress(string textBefore)
    {
        string run = textBefore[(textBefore.LastIndexOfAny([' ', '\n']) + 1)..];
        return run.Length > 0 && (".@/:".Contains(run[^1]) || run.Contains("www.") || run.Contains("://") || run.Contains('@'));
    }

    /// <summary>
    /// The full decision for a finished word, as the app makes it: the whole word read in the other mode, or an
    /// English + Korean split of the <paramref name="keys"/> as typed (capitals included). A whole-word Korean
    /// reading that no corpus has loses to a split: quantityrk is quantity가, not 벼무샤쇼가.
    /// In Korean mode a whole-word result comes back in lowercase, as the screen shows it.
    /// </summary>
    public string? Suggest(string word, string keys, Layout typedIn)
    {
        if (typedIn == Layout.Korean && !_isException(word))
        {
            // A name typed in Korean mode shows as broken Hangul; its keys carry the capital (Ndidi → ㅜ댜야).
            if (IsKnownName(keys) && !IsWellFormed(word)) return LayoutConverter.ToEnglish(word);
            // An acronym even when it happens to read as a syllable: Shift on S, D, N, A ... changes nothing in
            // Korean, so it was pressed for English (SEC → ㄴㄸㅊ, DNA → 움, a lone I → ㅑ).
            if (IsShiftedAcronym(keys)) return LayoutConverter.ToEnglish(word);
        }
        string? whole = Check(word, typedIn);
        if (whole is null) return CheckMixed(keys, typedIn);
        // Korean mode, leading capitals with Shift on a key without a shifted jamo (A, KT, ESPN): a rarer English reading of the whole word
        // (atl, ktrk, espndms) loses to capital(s) + Korean (A씨, KT가, ESPN은); a real word (Hello) stays whole.
        int leadingCapitals = keys.TakeWhile(char.IsAsciiLetterUpper).Count();
        if (typedIn == Layout.Korean && keys.Length >= 2 && HasEnglishOnlyShift(keys[..leadingCapitals])
            && _english(whole) < _o.CommonEnglish && CheckMixed(keys, typedIn) is { } capitalMixed)
            return capitalMixed;
        // Only a plain Hangul word no corpus has is in doubt; new Korean words are common, so it loses only to a
        // clear split: a common English word of four letters or more plus a particle.
        bool isDoubtful = typedIn == Layout.English && whole.All(Jamo.IsSyllable) && _korean(whole) <= 0;
        if (!isDoubtful || CheckMixed(keys, typedIn) is not { } mixed) return whole;
        int latin = mixed.TakeWhile(char.IsAsciiLetter).Count();
        bool isClearSplit = latin >= 4 && _english(mixed[..latin]) >= 4.0f && IsParticleChain(mixed[latin..]);
        return isClearSplit ? mixed : whole;
    }

    static bool IsLetter(char c) => char.IsAsciiLetter(c) || Jamo.IsHangul(c);

    string? CheckPart(string word, Layout typedIn)
    {
        // Length in keystrokes: 뭉 is three keys, a single letter is too ambiguous.
        int keys = KeyCount(word, typedIn);
        if (keys < _o.MinKeys) return null;
        return typedIn == Layout.English ? CheckEnglish(word) : CheckKorean(word);
    }

    static int KeyCount(string word, Layout typedIn) => typedIn == Layout.English ? word.Length : LayoutConverter.ToEnglish(word).Length;

    /// <summary>
    /// A word with anything besides letters in it (power-consumption, don't, "이번, 것"이라고, 시·군, 제2차, 5%로)
    /// is judged part by part, the other characters being the same key in both modes. It converts as a whole when
    /// some part converts and no other part is a real word as typed; one-key parts (the t of don't) just follow.
    /// A part right after a number is judged as what follows numbers: a particle, counter or common word (2차, 30분).
    /// </summary>
    string? CheckJoined(string word, Layout typedIn)
    {
        // English mode, letters then digits only: an ID (rlaalswn12 is 김민주 typed as an ID), not Korean.
        int trailingDigits = word.Length - word.TrimEnd("0123456789".ToCharArray()).Length;
        if (typedIn == Layout.English && trailingDigits > 0 && word[..^trailingDigits].All(char.IsAsciiLetter)) return null;
        if (typedIn == Layout.Korean && word.IndexOfAny(['\'', '’']) >= 0)
        {
            string english = LayoutConverter.ToEnglish(word);
            if (Contractions.Contains(english.Replace('’', '\'').ToLowerInvariant())) return english;
        }

        // Only everyday punctuation joins parts; other symbols (@ # $ & * = + < > | _ ...) mean a password-like string.
        if (word.Any(c => !IsLetter(c) && !char.IsAsciiDigit(c) && !JoinerPunctuation.Contains(c))) return null;
        // % only after a number (5%로).
        if (Enumerable.Range(0, word.Length).Any(i => word[i] == '%' && (i == 0 || !char.IsAsciiDigit(word[i - 1])))) return null;

        bool isKoreanJoin = typedIn == Layout.English && word.Any(c => c is '·' or 'ㆍ');
        var result = new System.Text.StringBuilder(word.Length);
        bool anyConverts = false, anyKept = false, anyWeakConversion = false;
        // What came right before the current piece: a number (2차), or something typed as meant that a particle
        // may follow (SDS가, )는, ’는).
        bool afterNumber = false, afterKept = false, afterEnglishWord = false;
        for (int i = 0; i < word.Length;)
        {
            int start = i;
            if (!IsLetter(word[i]))
            {
                // The other characters are the same key in both modes and stay as they are.
                while (i < word.Length && !IsLetter(word[i])) i++;
                string other = word[start..i];
                result.Append(other);
                afterNumber = char.IsAsciiDigit(other[^1]) || other[^1] == '%';
                afterKept = ")]}”’\"'".Contains(other[^1]);
                afterEnglishWord = false;
                continue;
            }
            if (typedIn == Layout.English && AcronymLength(word, i) is > 0 and var length)
            {
                // One capital between an English word and lowercase is CamelCase (CyberArk, PowerPoint), not an
                // acronym and a particle; 비타민C가 has Korean before the C.
                if (length == 1 && afterEnglishWord && i + 1 < word.Length && char.IsAsciiLetterLower(word[i + 1])) return null;
                // An acronym typed as meant stays English: 양해각서(MOU)를, 삼성SDS가, 비타민C가.
                result.Append(word, i, length);
                i += length;
                afterNumber = afterEnglishWord = false;
                afterKept = anyKept = true;
                continue;
            }
            while (i < word.Length && IsLetter(word[i]) && !(typedIn == Layout.English && AcronymLength(word, i) > 0)) i++;
            string part = word[start..i];
            string converted = typedIn == Layout.English ? LayoutConverter.ToKorean(part) : LayoutConverter.ToEnglish(part);
            bool isSuffix = typedIn == Layout.English && (afterNumber ? IsNumberSuffix(part) : afterKept && IsParticleChain(converted));
            // The middle dot joins Korean words only (시·도, 한·미): every part that reads as proper Hangul converts.
            bool isDotJoined = isKoreanJoin && IsWellFormed(converted);
            // Korean mode: proper Hangul after a number is Korean typed on purpose (4륜, 1녀).
            bool isKoreanAfterNumber = typedIn == Layout.Korean && afterNumber && IsWellFormed(part);
            // Letters right before a digit are a code (ch4가, mp3파일): English as typed. Between numbers they are
            // a Korean unit (1박2일, 7시30분).
            // Korean written before a number on purpose (제2차, 고3) is in Data/number-prefixes.txt.
            bool isCode = typedIn == Layout.English && !afterNumber && part.Length <= 4 && i < word.Length && char.IsAsciiDigit(word[i])
                && !NumberPrefixes.Contains(converted);
            bool converts = !isCode && (isSuffix || isDotJoined
                || (!(typedIn == Layout.English && afterNumber) && !isKoreanAfterNumber && CheckPart(part, typedIn) is not null));
            if (converts)
            {
                anyConverts = true;
                // Weak evidence: one syllable (au → 며 in Port-au-Prince) or Korean no corpus has (McVeigh → McV댜호).
                if (!isSuffix && !isDotJoined && typedIn == Layout.English && (converted.Length < 2 || _korean(converted) <= 0)) anyWeakConversion = true;
                result.Append(converted);
            }
            else if (isCode)
            {
                anyKept = true;
                result.Append(part);
            }
            // English typed as meant stays too: Center)에서, 홈페이지(www.
            else if (typedIn == Layout.English && _english(part) >= _o.KnownEnglish)
            {
                anyKept = true;
                result.Append(part);
                afterNumber = afterKept = false;
                afterEnglishWord = true;
                continue;
            }
            // A lowercase letter right before an acronym belongs to it: fMRI는, mRNA를, iOS가.
            else if (typedIn == Layout.English && part.Length == 1 && i < word.Length && AcronymLength(word, i) > 0)
            {
                anyKept = true;
                result.Append(part);
            }
            // One-key parts just follow (the t of don't).
            else if (KeyCount(part, typedIn) < _o.MinKeys)
                result.Append(converted);
            else
                return null;
            afterNumber = afterKept = afterEnglishWord = false;
        }
        // Next to English typed as meant, only strong conversions count: Port-au-Prince, McVeigh stay.
        if (anyKept && anyWeakConversion) return null;
        return anyConverts ? result.ToString() : null;
    }

    static readonly HashSet<char> JoinerPunctuation = ['-', '/', '\'', '"', '“', '”', '‘', '’', '`', '(', ')', '[', ']', '·', 'ㆍ', '%', '~', '…', '▲', '△', '—'];

    /// <summary>
    /// Length of an acronym starting at <paramref name="i"/>: two or more capitals, or one capital that is not a
    /// Korean shift key (the T of Tl is ㅆ, the C of 비타민C is English). 0 if none starts there.
    /// </summary>
    static int AcronymLength(string word, int i)
    {
        if (i > 0 && char.IsAsciiLetterUpper(word[i - 1])) return 0;
        int end = i;
        while (end < word.Length && char.IsAsciiLetterUpper(word[end])) end++;
        int length = end - i;
        if (length >= 2) return length;
        // A single capital: inside a word it is one (비타민C가); at the start of a word followed by lowercase it
        // just capitalizes the word (British, Henry's), alone it is a letter ((A)).
        bool isWordStart = i == 0 || !IsLetter(word[i - 1]);
        bool startsLowercaseWord = isWordStart && end < word.Length && char.IsAsciiLetterLower(word[end]);
        return length == 1 && IsEnglishOnlyCapital(word[i]) && !startsLowercaseWord ? 1 : 0;
    }

    /// <summary>Keys typed in English mode right after a number: 6보다, 3개, 1분기, 65세 — not 3rd, 4th, 10kg.</summary>
    bool IsNumberSuffix(string keys)
    {
        if (!keys.All(char.IsAsciiLetter) || HasEnglishOnlyShift(keys)) return false;
        string korean = LayoutConverter.ToKorean(keys);
        if (IsParticleChain(korean) || IsCounterChain(korean)) return true;
        // Any proper Korean that is not English (1차전에서, 2년전, 6자회담): English suffixes after numbers
        // (st, nd, rd, kg, pm, fps) read as broken jamo, and th (소) is English.
        if (!IsWellFormed(korean) || NumberSuffixes.Contains(keys.ToLowerInvariant())) return false;
        // One syllable: common in Korean and not common English (1심, 3루, 2군; not 4to → 4새). Capitals after a
        // number are English (222Rn, 1The).
        if (korean.Length == 1) return !keys.Any(char.IsAsciiLetterUpper) && _korean(korean) >= 4.0f && _english(keys) < 4.0f;
        // Longer: anything that is not English (1차전에서, 6자회담; not 4chan).
        return _english(keys) < _o.KnownEnglish;
    }

    // The hand-kept lists live in Data/*.txt so they can grow without code changes.
    static readonly HashSet<string> NumberSuffixes = WordList.Load("number-suffixes-en.txt");
    static readonly HashSet<string> NumberPrefixes = WordList.Load("number-prefixes.txt");

    /// <summary>English written after a number (th, st, kg, s), as in Data/number-suffixes-en.txt.</summary>
    public static bool IsEnglishNumberSuffix(string keys) => NumberSuffixes.Contains(keys.ToLowerInvariant());

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
        if (isRepeatedJamo || ChatAbbreviations.Contains(word) || IsAnonymized(word)) return null;

        string english = LayoutConverter.ToEnglish(word);
        float en = _english(english);
        // A compound no list has (autocompact, keyboardshortcut) is still English when the screen shows broken
        // Hangul and the keys are exactly two common words.
        if (en < _o.KnownEnglish) return !IsWellFormed(word) && IsEnglishCompound(english) ? english : null;
        // Other consonant-only/vowel-only strings could still be chat shorthand; only very common
        // English (ㅈㄷ → we, ㅛㅐㅕ → you) is worth converting.
        if ((word.All(Jamo.IsConsonant) || word.All(Jamo.IsVowel)) && en < _o.CommonEnglish) return null;

        if (!IsWellFormed(word)) return english;

        float ko = _korean(word);
        return ko < _o.CommonKorean && en - ko >= _o.KoreanToEnglishMargin ? english : null;
    }

    /// <summary>Capitals only (one to six), at least one of them on a key without a shifted jamo.</summary>
    // One syllable that follows a single capital in Korean writing (A조, B팀, C급, A씨).
    static readonly HashSet<string> CapitalSuffixes = WordList.Load("letter-suffixes.txt");

    bool IsWordAndParticles(string korean) => IsWellFormed(korean) && Enumerable.Range(2, Math.Max(0, korean.Length - 2))
        .Any(i => _korean(korean[..i]) >= 2.5f && IsParticleChain(korean[i..]));

    // How Korean writes an anonymous person or thing: a lone consonant, then 씨/양/군/사... and particles
    // (ㄱ씨는, ㄴ양, ㄷ사의) or particles alone (ㄱ이). A letter before a real word (e스포츠) is not this.
    static readonly HashSet<string> AnonymousHeads = WordList.Load("anonymous-heads.txt");

    bool IsAnonymized(string korean)
    {
        if (korean.Length < 2 || !Jamo.IsConsonant(korean[0])) return false;
        string rest = korean[1..];
        if (!IsWellFormed(rest)) return false;
        if (IsParticleChain(rest) || (AnonymousHeads.Contains(rest[..1]) && (rest.Length == 1 || IsParticleChain(rest[1..])))) return true;
        // Before a common word too (ㅅ아파트, ㄱ업체는, ㅇ교회), when the keys are no English word (ㅈ뭇 is want)
        // and the letter is not e or x, which English puts before words (e스포츠).
        return korean[0] is not ('ㄷ' or 'ㅌ') && _english(LayoutConverter.ToEnglish(korean)) < _o.KnownEnglish
            && (_korean(rest) >= _o.MixedKoreanRest || Enumerable.Range(1, rest.Length - 1)
                .Any(i => _korean(rest[..i]) >= _o.MixedKoreanRest && IsParticleChain(rest[i..])));
    }

    /// <summary>
    /// Korean mode: the English prefix happens to read as full syllables (suffix → 녈럍). Proper Korean wins
    /// unless those syllables are no Korean at all, the English word is long and common, and a particle
    /// follows: 녈럍라던가 is suffix라던가, while 갯녹음, 해미면 (rot, goal) stay Korean.
    /// </summary>
    bool IsUnknownSyllablesForEnglish(string prefix, string prefixKorean, string restKorean) =>
        prefix.Length >= 5 && _korean(prefixKorean) <= 0 && _english(prefix) >= _o.MixedEnglishPrefix && IsParticleChain(restKorean);

    static bool IsShiftedAcronym(string keys) =>
        keys.Length is >= 1 and <= 6 && keys.All(char.IsAsciiLetterUpper) && HasEnglishOnlyShift(keys);

    /// <summary>A capitalized word of four letters or more on the name list (Ndidi, Kowalczyk).</summary>
    bool IsKnownName(string word) =>
        word.Length >= 4 && char.IsAsciiLetterUpper(word[0]) && word.Skip(1).All(char.IsAsciiLetterLower) && _isName(word);

    bool IsEnglishCompound(string english) =>
        english.Length >= 7 && Enumerable.Range(3, english.Length - 5)
            .Any(i => _english(english[..i]) >= _o.MixedEnglishPrefix && _english(english[i..]) >= _o.MixedEnglishPrefix);

    // Particles and endings that get attached straight to an English word (shortage이다, update했다, Github에서는).
    static readonly HashSet<string> Particles = WordList.Load("particles.txt");

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
        if (typedIn == Layout.English && IsKnownName(keys)) return null;
        string display = typedIn == Layout.English ? keys : LayoutConverter.ToKorean(keys);
        if (_isException(display)) return null;
        // Korean mode: Shift on a key without a shifted jamo (S, N, ...) changes nothing in Korean, so it is
        // a sure sign of English (SNS typed there shows as the real word 눈).
        bool isShiftedForEnglish = typedIn == Layout.Korean && HasEnglishOnlyShift(keys);
        // English mode: capitals then lowercase (LArk, UNdp) is an acronym plus a particle, not the word
        // "lark" or "UNDP" that a case-insensitive lookup would find.
        bool isAcronymThenLower = keys.Length >= 3 && char.IsAsciiLetterUpper(keys[0]) && char.IsAsciiLetterUpper(keys[1])
            && keys.Any(char.IsAsciiLetterLower);
        float wholeScore = typedIn == Layout.English ? _english(keys) : 0f;
        bool isRealWord = typedIn == Layout.English
            ? !isAcronymThenLower && wholeScore >= _o.KnownEnglish
            : !isShiftedForEnglish && _korean(display) > 0;
        // A rare listed word (asms 1.7) may still be a lowercase letter + Korean (a는); a real one (isms 2.0) is
        // not split at all, nor a capitalized one: Drua (1.9) is a name, not D + 겸.
        string afterFirst = LayoutConverter.ToKorean(keys[1..]);
        bool isOnlySingleLetter = isRealWord && typedIn == Layout.English && wholeScore < 2.0f
            && (char.IsAsciiLetterLower(keys[0]) || (keys.Length >= 4 && IsParticleChain(afterFirst)) || CapitalSuffixes.Contains(afterFirst));
        if (isRealWord && !isOnlySingleLetter) return null;

        // Longest English prefix first. A code (mp3, ch4) ends at its last digit, so there the shortest prefix
        // that includes it goes first: mp3 + 파일, not mp3vk + 일.
        // One capital followed by lowercase (A씨는, K리그) is a common Korean way of writing and goes first,
        // so A씨는 does not become Atl + 는.
        int lastDigit = keys.LastIndexOfAny("0123456789".ToCharArray());
        bool isCapitalThenLower = IsEnglishOnlyCapital(keys[0]) && char.IsAsciiLetter(keys[1]) && !IsEnglishOnlyCapital(keys[1]);
        var splits = (lastDigit >= 0
            ? Enumerable.Range(lastDigit + 1, keys.Length - lastDigit - 1)
            : isCapitalThenLower
                ? Enumerable.Range(2, keys.Length - 2).Reverse().Prepend(1)
                : Enumerable.Range(2, keys.Length - 2).Reverse().Append(1)).ToList();   // a lowercase single letter last (a는)

        // Splits with solid evidence first; a prefix no word list knows (tcpview) only when none of them fits,
        // so CEO들의 is not taken as CEOemf + 의.
        return TrySplits(allowUnknownEnglish: false) ?? (typedIn == Layout.English ? TrySplits(allowUnknownEnglish: true) : null);

        string? TrySplits(bool allowUnknownEnglish)
        {
            foreach (int split in splits)
            {
                string prefix = keys[..split], rest = keys[split..];
                // A single letter: a capital (A씨는), or lowercase before a long particle or a word (a는, e스포츠) —
                // "a" + 가 or 해 alone is too ambiguous. A single digit is a number (6보다).
                bool isLowerLetter = prefix.Length == 1 && char.IsAsciiLetter(prefix[0]) && !IsEnglishOnlyCapital(prefix[0]);
                if (isOnlySingleLetter && prefix.Length != 1) continue;
                if (isLowerLetter && rest.Length < 3) continue;
                // Korean mode: a lone jamo before Korean is how news writes anonymous people (ㄱ씨는, ㄴ양); only a
                // capital on a key without a shifted jamo (A씨) is English.
                if (typedIn == Layout.Korean && !HasEnglishOnlyShift(prefix) && IsAnonymized(LayoutConverter.ToKorean(prefix) + LayoutConverter.ToKorean(rest))) continue;
                // A particle typed by mistake is plain lowercase letters (Shift only for ㄲㄸㅃㅆㅉㅒㅖ); INGKA is not IN + 함.
                if (!rest.All(char.IsAsciiLetter) || HasEnglishOnlyShift(rest)) continue;
                // Korean mode: English typed there shows up as broken Hangul (놳ㅁㅎㄷ). Proper syllables mean
                // the user was typing Korean on purpose: 추가됩니다 is not "cn" + 가됩니다.
                // A syllable reading that no corpus has, of a common English word, is still English (suffix → 녈럍라던가).
                string prefixKorean = LayoutConverter.ToKorean(prefix);
                if (typedIn == Layout.Korean && !HasEnglishOnlyShift(prefix) && IsWellFormed(prefixKorean)
                    && !IsUnknownSyllablesForEnglish(prefix, prefixKorean, LayoutConverter.ToKorean(rest))) continue;
                // Letters with digits (ch4 = chapter 4, mp3, h2o) are codes, never Korean typing; no word list has them.
                // Short only: a long letters+digits run before "rk" is more likely a password-like string.
                bool isCode = prefix.Length <= 6 && prefix.Any(char.IsAsciiDigit) && prefix.Any(char.IsAsciiLetter);
                bool isNumber = prefix.All(char.IsAsciiDigit);
                if (prefix.Any(char.IsAsciiDigit) && !isCode && !isNumber) continue;
                string korean = LayoutConverter.ToKorean(rest);
                // A number takes a particle, a counter or any proper Korean (6보다, 3개, 1차전에서).
                if (isNumber)
                {
                    if (IsNumberSuffix(rest)) return prefix + korean;
                    continue;
                }
                // An all-caps prefix is a deliberate acronym (SNS를, FTA가, B형) even when the word lists miss it.
                bool isAcronym = prefix.All(char.IsAsciiLetterUpper);
                // Codes and capitals inside a word (OpenGL) are deliberate too, but only before a safe particle:
                // BioLargo is not BioLar + 해.
                bool isDeliberate = isCode || HasInnerCapital(prefix);
                bool isParticle = IsParticleChain(korean);
                bool isLongParticle = isParticle && rest.Length >= 3;
                // A rarer English word (giffen, engel, Lancet) is enough when the evidence is strong: broken Hangul in
                // Korean mode, a particle of three or more keys (dms 은, fmf 를, dptj 에서) that English words do not end
                // in, or one of the two-key particles they rarely end in (dl 이, dp 에, eh 도, fh 로). The others
                // (di 야, ek 다, rk 가, ak 만) need a common word, or names split: Namadi is not Nama + 야, Novak not Nov + 만.
                bool isSafeParticle = isLongParticle || korean is "이" or "에" or "도" or "로";
                // 가 (rk) sits between: English words ending in rk (work, park, Clark) are nearly all listed, so a
                // rarer word of four letters or more will do (rebate 2.8 + 가), while Novak + 만 still needs more.
                float needed = typedIn == Layout.Korean || isSafeParticle ? _o.KnownEnglish
                    : korean == "가" && prefix.Length >= 4 ? 2.5f
                    : _o.MixedEnglishPrefix;
                // A lowercase word that cannot be read as Korean at all is English even if no list has it
                // (tcpview, Jagdtiger), when a long particle follows.
                bool isUnknownEnglish = allowUnknownEnglish && isLongParticle && prefix.Length > 1
                    && prefix[1..].All(char.IsAsciiLetterLower) && !IsWellFormed(LayoutConverter.ToKorean(prefix));
                if (isLowerLetter && !(isLongParticle || (korean.Length >= 2 && IsWellFormed(korean) && _korean(korean) >= _o.MixedKoreanRest))) continue;
                bool isVouched = isAcronym || isCode || (isDeliberate && isSafeParticle) || isUnknownEnglish || isLowerLetter;
                if (!isVouched && _english(prefix) < needed) continue;
                // In English mode the rest must not be common English itself: Upwork is not Up + 재가 ("work"),
                // while abbreviations and slang (wh in A조, gud in B형) do not count.
                bool isRestEnglish = typedIn == Layout.English && _english(rest) >= 4.0f;
                // A common word of two or more syllables (engel곡선, LG전자), or after an acronym also one syllable
                // (B형, FA컵, PC방); a lone syllable after a plain word is too easily the tail of a name (Durango → Duran해).
                bool isCommonWord = IsWellFormed(korean) && !isRestEnglish
                    && (korean.Length >= 2 ? _korean(korean) >= _o.MixedKoreanRest
                        // after a single capital only a full syllable of three keys (B형, B팀), not Gwo → G재
                        : isAcronym && (prefix.Length >= 2 || rest.Length >= 3 || CapitalSuffixes.Contains(korean)) && _korean(korean) >= 3.0f)
                    // After an acronym also a rarer word + particle: SK텔레콤은, LG유플러스는 (텔레콤 2.6).
                    || (isAcronym && prefix.Length >= 2 && (IsWordAndParticles(korean) || (korean.Length >= 2 && IsWellFormed(korean) && _korean(korean) >= 2.5f)));
                if (isParticle || isCommonWord) return prefix + korean;
            }
            return null;
        }
    }

    // Counters that follow a number (3개, 10분, 2024년).
    static readonly HashSet<string> Counters = WordList.Load("counters.txt");

    // One or two counters, optionally followed by particles: 개, 개가, 분에서, 여명이 (여 + 명 + 이).
    static bool IsCounterChain(string text, int depth = 0)
    {
        for (int length = Math.Min(3, text.Length); length >= 1; length--)
        {
            if (!Counters.Contains(text[..length])) continue;
            string after = text[length..];
            if (after.Length == 0 || IsParticleChain(after) || (depth == 0 && IsCounterChain(after, 1))) return true;
        }
        return false;
    }

    // A capital after a lowercase letter: OpenGL, YouTube — clearly typed as English.
    static bool HasInnerCapital(string word) =>
        Enumerable.Range(1, Math.Max(0, word.Length - 1)).Any(i => char.IsAsciiLetterLower(word[i - 1]) && IsEnglishOnlyCapital(word[i]));

    static bool IsEnglishOnlyCapital(char c) => char.IsAsciiLetterUpper(c) && !KoreanShiftKeys.Contains(c);

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

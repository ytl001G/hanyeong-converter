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
        // ㄹㄷ (fe), ㄽ (ft): jamo-only words are no chat shorthand in the middle of English.
        CommonEnglish = 1.5f,
        // Nor is a listed chat abbreviation whose keys are a common word (ㅎㄷㄷ is gee, ㄱㄷ is re); ㅋㅋ still is.
        ChatAbbreviationEnglish = 4.0f,
    };

    /// <summary>
    /// The opposite: an earlier word of this sentence was a sure word left as typed (the, with, 그리고), so the user
    /// writes in this mode. A short word after it (en, di) changes only when the other reading is far more likely.
    /// </summary>
    public static readonly AutoDetectorOptions Staying = Default with
    {
        EnglishToKoreanMargin = 3f,
        KoreanToEnglishMargin = 5f,
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
    /// <summary>A listed chat abbreviation (ㅇㅋ, ㅎㄷㄷ) converts only when its keys are English this common.</summary>
    public float ChatAbbreviationEnglish { get; init; } = float.MaxValue;
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
    readonly Func<string, bool> _looksEnglish;
    readonly AutoDetectorOptions _o;

    public AutoDetector(Func<string, bool>? isException = null, AutoDetectorOptions? options = null)
        : this(w => WordFrequency.English.Value.Score(w), w => WordFrequency.Korean.Value.Score(w), isException, options,
            w => NameList.Default.Value.Contains(w), w => WordFrequency.English.Value.LooksLikeWord(w))
    {
    }

    public AutoDetector(Func<string, float> english, Func<string, float> korean, Func<string, bool>? isException = null,
        AutoDetectorOptions? options = null, Func<string, bool>? isName = null, Func<string, bool>? looksEnglish = null)
    {
        _isName = isName ?? (_ => false);
        _looksEnglish = looksEnglish ?? (_ => false);
        // The word tables start at two letters; the only one-letter English words are added here.
        _english = w => w.Length == 1 ? (w is "a" or "A" or "i" or "I" ? 7.5f : 0f) : english(w);
        _korean = korean;
        _isException = isException ?? (_ => false);
        _o = options ?? AutoDetectorOptions.Default;
    }

    /// <summary>A word of the mode it was typed in: listed English, or Korean some corpus has.</summary>
    public bool IsKnownWord(string word, Layout typedIn) => typedIn == Layout.English ? _english(word) >= _o.KnownEnglish : _korean(word) > 0;

    /// <summary>The replacement for <paramref name="word"/> (as shown on screen), or null to leave it.</summary>
    public string? Check(string word, Layout typedIn)
    {
        if (_isException(word)) return null;
        if (typedIn == Layout.English && IsKnownName(word) && !IsKoreanWithShift(word)) return null;
        if (!word.All(IsLetter)) return CheckJoined(word, typedIn);
        // All letters, but with an acronym inside (삼성SDS가, 비타민C가): judged in parts like a joined word.
        string? result = CheckPart(word, typedIn)
            ?? (typedIn == Layout.English && word.Any(char.IsAsciiLetterLower)
                && Enumerable.Range(0, word.Length).Any(i => AcronymLength(word, i) > 0)
                ? CheckJoined(word, typedIn) : null);
        return result;
    }

    // A known word, or one + particles (또라는, 꾸준한) even if that exact form is in no corpus.
    bool IsKoreanWord(string korean) =>
        _korean(korean) > 0 || IsWordAndParticles(korean) || (korean.Length >= 3 && _korean(korean[..^1]) > 0);

    // Shift on the first key for ㄲ ㄸ ㅃ ㅆ ㅉ ㅒ ㅖ, and the word reads as Korean: Rnwnsgks is 꾸준한, not a name.
    bool IsKoreanWithShift(string keys) =>
        KoreanShiftKeys.Contains(keys[0]) && LayoutConverter.ToKorean(keys) is var k && IsWellFormed(k) && IsKoreanWord(k);

    static bool IsCapitalized(string word) =>
        word.Length >= 2 && char.IsAsciiLetterUpper(word[0]) && word.Skip(1).All(char.IsAsciiLetterLower);

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
    /// A decimal point or the colon of a time is no address: 0. then <paramref name="word"/> 4akszma is 0.4만큼, 5: then
    /// 22Rkwl is 5:22까지.
    /// </summary>
    public static bool IsInAddress(string textBefore, string word = "")
    {
        string run = textBefore[(textBefore.LastIndexOfAny([' ', '\n']) + 1)..];
        if (IsNumberJoin(run, word)) return false;
        return run.Length > 0 && (".@/:".Contains(run[^1]) || run.Contains("www.") || run.Contains("://") || run.Contains('@'));
    }

    // A number with one point or colon, a sign or currency in front (0. -1. $3. 10:), and digits after it.
    static bool IsNumberJoin(string run, string word)
    {
        if (word.Length == 0 || !char.IsAsciiDigit(word[0]) || run.Length < 2 || run[^1] is not ('.' or ':')) return false;
        string number = run[..^1].TrimStart(NumberSigns);
        return number.Length > 0 && number.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// The full decision for a finished word, as the app makes it: the whole word read in the other mode, or an
    /// English + Korean split of the <paramref name="keys"/> as typed (capitals included). A whole-word Korean
    /// reading that no corpus has loses to a split: quantityrk is quantity가, not 벼무샤쇼가.
    /// In Korean mode a whole-word result comes back in lowercase, as the screen shows it.
    /// </summary>
    public string? Suggest(string word, string keys, Layout typedIn)
    {
        // A hashtag is judged by the word after # (#맛집). A mention too in Korean mode (@쟈ㅜㅎ is @wing); in English
        // mode it is someone's ID, typed as meant.
        if (word.Length > 1 && (word[0] == '#' || (word[0] == '@' && typedIn == Layout.Korean)) && !"#@".Contains(word[1]))
        {
            if (Suggest(word[1..], keys[1..], typedIn) is { } tagged) return word[0] + tagged;
            // An ID after @ is English: broken jamo there (@ㄷㅎㅎ is @egg) converts even when short, unless chat (@ㅋㅋ).
            string id = word[1..];
            bool isChat = (id.Length >= 2 && id.All(c => c == id[0])) || ChatAbbreviations.Contains(id);
            return word[0] == '@' && id.All(Jamo.IsHangul) && !IsWellFormed(id) && !isChat ? word[0] + LayoutConverter.ToEnglish(id) : null;
        }
        // Korean mode, quotes or brackets around a word ("I, (So): the word inside is judged as it is.
        if (typedIn == Layout.Korean && Wrapped(word) is var (lead, trail) && lead + trail > 0)
            return Suggest(word[lead..^trail], keys[lead..^trail], typedIn) is { } inner ? word[..lead] + inner + word[^trail..] : null;
        if (typedIn == Layout.Korean && !_isException(word))
        {
            // A name typed in Korean mode shows as broken Hangul; its keys carry the capital (Ndidi → ㅜ댜야).
            // Also a longer name that happens to read as syllables that are no Korean word (Endeley → 뚱디됴); four
            // letters meet Korean too often (Rosl is 깨니).
            if (IsKnownName(keys) && (!IsWellFormed(word) || (keys.Length >= 5 && !IsKoreanWord(word)))) return LayoutConverter.ToEnglish(word);
            // camelCase (isImmunity → ㅑ냐ㅡㅡㅕㅜㅑ쇼, eurosMr → 뎌개늑): a capital without a shifted jamo after
            // lowercase and before lowercase, when the syllables make no Korean word. Not an acronym inside (fMRI는).
            bool isCamelCase = keys.All(char.IsAsciiLetter) && HasInnerCapital(keys)
                && Enumerable.Range(0, keys.Length).All(i => !char.IsAsciiLetterUpper(keys[i]) || (i + 1 < keys.Length && char.IsAsciiLetterLower(keys[i + 1])));
            // One syllable among broken jamo makes no word (디 in BelongedSr → ㅠ디ㅐㅜㅎㄷㅇㄴㄱ).
            if (isCamelCase && !(word.Count(Jamo.IsSyllable) >= 2 && IsKoreanInSyllables(word)))
                return LayoutConverter.ToEnglish(word);
            // An acronym even when it happens to read as a syllable: Shift on S, D, N, A ... changes nothing in
            // Korean, so it was pressed for English (SEC → ㄴㄸㅊ, DNA → 움, a lone I → ㅑ).
            if (IsShiftedAcronym(keys)) return LayoutConverter.ToEnglish(word);
            // A capitalized word with Shift on such a key (Anaut → 무몃, Aozmendi → 매크두야): a name or a sentence start.
            // Not a word + particles (A씨: the T of ㅆ is shifted too). Two letters when a common word (So → 내, Go → 해).
            // A common English word of five letters or more beats a capital + Korean (Saudi 4.4 is not S + 며야); a
            // shorter one does not (G에, not Gdp 4.0).
            if ((keys.Length >= 3 || (keys.Length == 2 && _english(keys) >= _o.CommonEnglish))
                && IsEnglishOnlyCapital(keys[0]) && keys.Skip(1).All(char.IsAsciiLetterLower)
                && ((keys.Length >= 5 && _english(keys) >= 4.0f) || !(CheckMixed(keys, typedIn) is { } split && IsKeptSplit(split))))
                return LayoutConverter.ToEnglish(word);
        }
        string? whole = Check(word, typedIn);
        if (whole is null) return CheckMixed(keys, typedIn);
        // Korean mode: two words read as a compound no list has (utility + sms) lose to a word + clear ending (utility는).
        if (typedIn == Layout.Korean && _english(whole) < _o.KnownEnglish && CheckMixed(keys, typedIn) is { } withEnding
            && IsClearEnding(withEnding.SkipWhile(c => !Jamo.IsHangul(c))))
            return withEnding;
        // Korean mode, leading capitals with Shift on a key without a shifted jamo (A, KT, ESPN): a rarer English reading of the whole word
        // (atl, ktrk, espndms) loses to capital(s) + Korean (A씨, KT가, ESPN은); a real word (Hello) stays whole.
        int leadingCapitals = keys.TakeWhile(char.IsAsciiLetterUpper).Count();
        if (typedIn == Layout.Korean && keys.Length >= 2 && HasEnglishOnlyShift(keys[..leadingCapitals])
            && _english(whole) < 3.0f && CheckMixed(keys, typedIn) is { } capitalMixed)
            return capitalMixed;
        // Only a plain Hangul word no corpus has is in doubt; new Korean words are common, so it loses only to a
        // clear split: a common English word of four letters or more plus a particle.
        bool isDoubtful = typedIn == Layout.English && whole.All(Jamo.IsSyllable) && _korean(whole) <= 0;
        if (!isDoubtful || CheckMixed(keys, typedIn) is not { } mixed) return whole;
        int latin = mixed.TakeWhile(char.IsAsciiLetter).Count();
        string particles = mixed[latin..];
        return IsEnglishBeforeParticles(mixed[..latin], whole[..^particles.Length], particles) ? mixed : whole;
    }

    // Opening and closing quotes/brackets around Hangul only, as counts; null when the inside is not plain Hangul.
    static (int Lead, int Trail)? Wrapped(string word)
    {
        int lead = word.TakeWhile(c => "\"'([“‘".Contains(c)).Count();
        int trail = word[lead..].Reverse().TakeWhile(c => "\"')]”’".Contains(c)).Count();
        return word.Length - lead - trail > 0 && word[lead..^trail].All(Jamo.IsHangul) ? (lead, trail) : null;
    }

    /// <summary>
    /// English + particles, or one Korean word? Both sides are weighed, in either mode: the English part must be a
    /// word (a rarer one before particles English seldom ends in: three keys or more take any listed word, gyri는;
    /// 이 에 도 로 one of 2.5, gyrus에; a listed name counts), and the same keys read as Korean must not be a Korean
    /// word (해소시켜, 처리됩니다) unless the English is far more common (work하려고: work 5.9 beats 재가).
    /// </summary>
    bool IsEnglishBeforeParticles(string english, string stemKorean, string particles, bool isKoreanMode = false)
    {
        // Proper syllables typed in Korean mode are stronger evidence: five letters and a common word at least
        // (해미면 is not goal + 면, 치명적이므로 not claud + 적이므로).
        if (english.Length < (isKoreanMode ? 5 : 4) || !IsParticleChain(particles)) return false;
        int particleKeys = LayoutConverter.ToEnglish(particles).Length;
        float needed = isKoreanMode ? _o.MixedEnglishPrefix
            : particleKeys >= 3 ? _o.KnownEnglish : particles is "이" or "에" or "도" or "로" ? 2.5f : 4.0f;
        bool isEnglish = _english(english) >= needed || (particleKeys >= 2 && IsKnownName(english));
        float stemScore = _korean(stemKorean);
        bool isKoreanStem = stemScore > 0 && stemScore >= _english(english) - 1.0f;
        return isEnglish && !isKoreanStem;
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
            string plain = english.Replace('’', '\'').ToLowerInvariant();
            if (Contractions.Contains(plain)) return english;
            // A possessive: company's shows as 채ㅡㅔ무ㅛ'ㄴ. The word before it reads as broken Hangul, or is listed
            // English (Ab's → 뮤'ㄴ); Korean puts no lone ㄴ after a quote.
            if (plain.EndsWith("'s") && word[..^2] is { Length: > 0 } stem && stem.All(Jamo.IsHangul)
                && (!IsWellFormed(stem) || _english(plain[..^2]) >= _o.KnownEnglish))
                return english;
        }
        // English mode: a possessive is English as typed (ep's is not 데'ㄴ); Korean keys never end in 's.
        if (typedIn == Layout.English && word.Replace('’', '\'').EndsWith("'s") && word[..^2].All(char.IsAsciiLetter)) return null;

        // Only everyday punctuation joins parts; other symbols (@ # $ & * = + < > | _ ...) mean a password-like string,
        // or in Korean mode an email or code name typed there: all its Hangul broken (ㅕㄴㄷㄱ9@흐먀ㅣ is user9@gmail).
        if (word.Any(c => !IsLetter(c) && !char.IsAsciiDigit(c) && !JoinerPunctuation.Contains(c)))
            return typedIn == Layout.Korean ? BrokenHangulAsEnglish(word) : null;
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
                afterKept = ")]}”’\"'+".Contains(other[^1]);
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
            // Korean mode: a lone jamo after a number is an English ending (75ㄴ → 75s, 3ㄱㅇ → 3rd).
            bool isEnglishEnding = typedIn == Layout.Korean && afterNumber && !IsWellFormed(part) && NumberSuffixes.Contains(converted.ToLowerInvariant());
            // ...and 소 at the end of a number that takes th (17소 → 17th, 80소, 12소; not 1소, 22소): Korean puts no 소 there.
            isEnglishEnding |= typedIn == Layout.Korean && afterNumber && part == "소" && i == word.Length && TakesTh(word[..start]);
            // Letters right before a digit are a code (ch4가, mp3파일): English as typed. Between numbers they are
            // a Korean unit (1박2일, 7시30분).
            // Korean written before a number on purpose (제2차, 고3) is in Data/number-prefixes.txt.
            bool isCode = typedIn == Layout.English && !afterNumber && part.Length <= 4 && i < word.Length && char.IsAsciiDigit(word[i])
                && !NumberPrefixes.Contains(converted);
            bool converts = !isCode && (isSuffix || isDotJoined || isEnglishEnding
                || (!(typedIn == Layout.English && afterNumber) && !isKoreanAfterNumber && CheckPart(part, typedIn) is not null));
            if (converts)
            {
                anyConverts = true;
                // Weak evidence: one syllable (au → 며 in Port-au-Prince) or Korean no corpus has (McVeigh → McV댜호).
                // A long compound no corpus has is still Korean (중동호흡기증후군(MERS, 목포투데이(www).
                bool isBeforeBracket = i < word.Length && word[i] is '(' or '[';
                if (!isSuffix && !isDotJoined && typedIn == Layout.English
                    && ((converted.Length < 2 && !(isBeforeBracket && _korean(converted) >= 4.0f))
                        || (_korean(converted) <= 0 && (converted.Length < 4 || part.Any(char.IsAsciiLetterUpper)))))
                    anyWeakConversion = true;
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
            // A part may be English + particle itself: pre-stimulus가 (stimulus + 가), in either mode. Only between
            // punctuation: an acronym inside letters (ATldml) is no joined word, and CheckMixed reads it whole (A씨의).
            // It counts as a change only when it changes something, and not from one lowercase letter (ㅠ-ㅠ무서워요 is no
            // b + 무서워요; (A씨는 still converts).
            else if (word.Any(JoinerPunctuation.Contains) && CheckMixed(typedIn == Layout.English ? part : LayoutConverter.ToEnglish(part), typedIn) is { } mixedPart
                && !(mixedPart.TakeWhile(char.IsAsciiLetter).Count() == 1 && char.IsAsciiLetterLower(mixedPart[0])))
            {
                if (mixedPart != part) anyConverts = true;
                result.Append(mixedPart);
            }
            else
                return null;
            afterNumber = afterKept = afterEnglishWord = false;
        }
        // Next to English typed as meant, only strong conversions count: Port-au-Prince, McVeigh stay.
        if (anyKept && anyWeakConversion) return null;
        return anyConverts ? result.ToString() : null;
    }

    static readonly HashSet<string> DottedAbbreviations = WordList.Load("dotted-abbreviations.txt");

    /// <summary>
    /// Korean mode, a period just typed after <paramref name="run"/> (what was typed since the last space): the
    /// English when the run is a dotted abbreviation typed there (ㅁ.ㅡ → a.m, ㅡㄱ → mr), each part broken Hangul.
    /// <paramref name="keys"/> are its keys as typed, capitals included (ㅕ.ㄴ is U.S).
    /// </summary>
    public static string? DottedAbbreviation(string run, string keys)
    {
        if (run.Length == 0 || !run.Split('.').All(p => p.Length > 0 && p.All(Jamo.IsHangul) && !IsWellFormed(p))) return null;
        return DottedAbbreviations.Contains(keys.ToLowerInvariant()) ? keys : null;
    }

    /// <summary>
    /// Korean mode: the English keys of text whose every run of Hangul is broken (ㅕㄴㄷㄱ, 흐먀ㅣ, ㅊㄱㅁ지), as English
    /// typed there shows; null when a run reads as Korean or as chat (ㅋㅋ, ㅠㅠ, ㅇㅋ, or up to three consonants or
    /// vowels alone, as in ^^ㅋ).
    /// </summary>
    public static string? BrokenHangulAsEnglish(string text)
    {
        var runs = new string(text.Select(c => Jamo.IsHangul(c) ? c : ' ').ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (runs.Length == 0 || runs.All(IsWellFormed)) return null;
        // Chat-like: a listed abbreviation or up to three consonants (ㅇㅋ, ㄷㄷ); mixed vowels are keys (ㅡㅑㅜ is min).
        static bool IsChatLike(string run) => ChatAbbreviations.Contains(run) || (run.Length <= 3 && run.All(Jamo.IsConsonant));
        foreach (string run in runs)
        {
            // Proper syllables may be English keys too (gb → 휴 in vacuum_gb), but not a Korean word of two syllables or more.
            if (IsWellFormed(run) && run.Length >= 2 && WordFrequency.Korean.Value.Score(run) > 0) return null;
            // A jamo repeated is chat wherever it is (ㅋㅋ, ㅠㅠ).
            if (run.Length >= 2 && Jamo.IsJamo(run[0]) && run.All(c => c == run[0])) return null;
        }
        // Chat-like pieces only (^^ㅋ, ㅇㅋ!) stay; next to a piece of broken English they are keys too (memories_wet).
        if (runs.All(IsChatLike)) return null;
        return LayoutConverter.ToEnglish(text);
    }

    // A number whose English ordinal ends in th: last digit 0 or 4-9, or 11-13.
    static bool TakesTh(string before)
    {
        string digits = new(before.Reverse().TakeWhile(char.IsAsciiDigit).Reverse().ToArray());
        if (digits.Length == 0) return false;
        return (digits.Length >= 2 && digits[^2] == '1') || digits[^1] is '0' or >= '4';
    }

    // + too: A+와, C++를, +와.
    static readonly HashSet<char> JoinerPunctuation = ['-', '/', '\'', '"', '“', '”', '‘', '’', '`', '(', ')', '[', ']', '·', 'ㆍ', '%', '~', '…', '▲', '△', '—', '+'];

    // Written in front of a number: a sign or a currency.
    static readonly char[] NumberSigns = ['-', '+', '$', '€', '£', '¥', '₩'];

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
        // Shifted Korean keys right after a lowercase letter are jamo (dPQmrp is 예쁘게: ㅖ ㅃ), not an acronym.
        if (i > 0 && char.IsAsciiLetterLower(word[i - 1]) && word[i..end].All(c => KoreanShiftKeys.Contains(c))) return 0;
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
        if (!word.All(char.IsAsciiLetter)) return null;
        // Capitals other than Korean shift keys usually mean a real English word, name or acronym. One slip is kept:
        // Shift held on past a key that needs it (the T of ㅆ, then M), never without one (dkssUD). Released inside
        // a syllable, it is a slip anywhere: TMs거야 is 쓴거야 (the s is the 받침 of 쓴), as no one lets go of Shift
        // there on purpose. Released between syllables, it may be an acronym + particle (RK는, SK는), so only inside
        // a long word: qlrygkslRK is 비교하니까.
        if (HasEnglishOnlyShift(word))
        {
            string unshifted = new(word.Select(c => IsEnglishOnlyCapital(c) ? char.ToLowerInvariant(c) : c).ToArray());
            string reading = LayoutConverter.ToKorean(unshifted);
            if (ShiftHeldOn(word, unshifted) is not { } held || !IsWellFormed(reading) || !IsKoreanWord(reading)
                || _english(word) >= _o.KnownEnglish) return null;
            return held.InsideSyllable ? (reading.Length >= 2 ? reading : null)
                : held.AfterStart && reading.Length >= 4 ? reading : null;
        }

        string korean = LayoutConverter.ToKorean(word);
        // Chat jamo typed in English mode (dd → ㅇㅇ, zz → ㅋㅋ) when the English is not common (gg 6.5 stays);
        // stricter in a sentence of English, looser in one of Korean.
        if (word.All(char.IsAsciiLetterLower) && IsChatJamo(korean, _english(word), 5.0f - _o.EnglishToKoreanMargin)) return korean;
        // An anonymous person written in Korean (rTlsms → ㄱ씨는) when the keys are no English word. English mode asks
        // for a lowercase first key (E클래스, T세포: a capital is English, and no one writes ㄸ씨) and 씨/양/군... after
        // it: a letter + a word is more often meant (q스킬, w쓰고, t스토어), and ㄵ노 (swsh) is random keys.
        if (IsAnonymized(korean) && _english(word) < _o.KnownEnglish && char.IsAsciiLetterLower(word[0])
            && Jamo.IsConsonant(korean[0]) && !IsCluster(korean[0]) && AnonymousHeads.Contains(korean[1..2]))
            return korean;
        // Proper syllables, or a listed Korean word with a rarer one (여깄어요: 깄 is outside KS X 1001).
        if (!IsWellFormed(korean) && !(korean.All(Jamo.IsSyllable) && _korean(korean) > 0)) return null;

        float en = _english(word);
        float ko = _korean(korean) is var k && k > 0 ? k : _o.UnseenKorean;
        return ko - en >= _o.EnglishToKoreanMargin ? korean : null;
    }

    string? CheckKorean(string word)
    {
        if (!word.All(Jamo.IsHangul)) return null;
        // ㅋㅋㅋ, ㅠㅠ, ㅇㅋ, ㄱㅅ: chat expressions, not typos.
        bool isRepeatedJamo = word.Length >= 2 && Jamo.IsJamo(word[0]) && word.All(c => c == word[0]);
        // ...except ㅈㅈㅈ, which is www (ㅉㅈㅈ with Shift for a capital: Www at the start of a sentence).
        if (word is "ㅈㅈㅈ" or "ㅉㅈㅈ") return LayoutConverter.ToEnglish(word);
        string english = LayoutConverter.ToEnglish(word);
        float en = _english(english);
        if (isRepeatedJamo || (ChatAbbreviations.Contains(word) && en < _o.ChatAbbreviationEnglish) || IsAnonymized(word)) return null;
        // A compound no list has (autocompact, keyboardshortcut) is still English when the screen shows broken
        // Hangul and the keys are exactly two common words.
        if (en < _o.KnownEnglish) return !IsWellFormed(word) && IsEnglishCompound(english) ? english : null;
        // Other consonant-only/vowel-only strings could still be chat shorthand; only very common
        // English (ㅈㄷ → we, ㅛㅐㅕ → you) is worth converting.
        // Chat shorthand is short (ㅇㅋ, ㄱㅅ, ㅎㄷㄷ); four jamo and more are a word typed in the wrong mode (feat → ㄹㄷㅁㅅ).
        // Vowels only are chat when one repeats (ㅠㅠ, ㅡㅡ) or listed (ㅠㅜ); mixed ones are English keys (ㅙㅐㅏ is hook).
        bool isChatVowels = word.All(Jamo.IsVowel) && (word.All(c => c == word[0]) || ChatAbbreviations.Contains(word));
        if (word.Length <= 3 && (word.All(Jamo.IsConsonant) || isChatVowels) && en < _o.CommonEnglish) return null;

        if (!IsWellFormed(word)) return english;

        float ko = _korean(word);
        return ko < _o.CommonKorean && en - ko >= _o.KoreanToEnglishMargin ? english : null;
    }

    /// <summary>Capitals only (one to six), at least one of them on a key without a shifted jamo.</summary>
    // One syllable that follows a single capital in Korean writing (A조, B팀, C급, A씨).
    static readonly HashSet<string> CapitalSuffixes = WordList.Load("letter-suffixes.txt");

    /// <summary>A Korean word + particles or endings (진행상황에, 확인했어): a sentence word, not a name.</summary>
    public bool IsWordWithEnding(string korean) => IsWordAndParticles(korean);

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
        // Not when the keys are common English (ㅁ해 is ago; ㄱ씨 is rtl, a rare abbreviation).
        if (_english(LayoutConverter.ToEnglish(korean)) >= _o.CommonEnglish) return false;
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

    // A split of a capitalized word typed in Korean mode that is kept: capitals + Korean (K리그, B형, A씨), or a
    // word of three letters and more + a clear ending; not Co + 구디 (Cornel) or Aj + 만 (Ajaks).
    bool IsKeptSplit(string split)
    {
        string latin = string.Concat(split.TakeWhile(c => !Jamo.IsHangul(c)));
        string ending = split[latin.Length..];
        // Before a two-key ending a name is as likely (Dikeh, not Dik + 도), as in English mode.
        bool isWordEnough = LayoutConverter.ToEnglish(ending).Length >= 3 || _english(latin) >= 3.0f;
        return latin.All(char.IsAsciiLetterUpper) || (latin.Length >= 3 && IsClearEnding(ending) && isWordEnough);
    }

    // A Korean ending that English words do not end in: three keys or more, or 이 에 도 로 (Ansal + 야 is a name).
    static bool IsClearEnding(IEnumerable<char> hangul)
    {
        string korean = string.Concat(hangul);
        return IsParticleChain(korean) && (LayoutConverter.ToEnglish(korean).Length >= 3 || korean is "이" or "에" or "도" or "로");
    }

    // ㅋㅋ ㅎㅎ ㅇㅇ ㅠㅠ ㄷㄷ: a jamo chat repeats (not ㅔㅔ, which is pp, or ㄹㄹ). Abbreviations like ㅇㅋ are
    // left out: their keys are English too often (dz, wt).
    static bool IsChatJamo(string korean, float english, float below) =>
        korean.Length >= 2 && korean.All(c => c == korean[0]) && "ㅋㅎㅇㅠㅜㄷㄱㅂㄴ".Contains(korean[0]) && english < below;

    // Every capital without a shifted jamo follows a capital, in a run that starts on a Korean shift key after
    // lowercase: Shift was pressed for ㄲ and held (qlrygkslRK), not for the capital itself.
    // Each run of capitals with one on a key without a shifted jamo must start on a Korean shift key: Shift was
    // pressed for ㄲ ㅆ ... and held. Null when one does not. InsideSyllable: Shift let go within a syllable (TM + s of
    // 쓴); AfterStart: no run at the start of the word.
    static (bool InsideSyllable, bool AfterStart)? ShiftHeldOn(string word, string unshifted)
    {
        string reading = LayoutConverter.ToKorean(unshifted);
        bool insideSyllable = false, afterStart = true;
        for (int i = 0; i < word.Length; i++)
        {
            if (!char.IsAsciiLetterUpper(word[i])) continue;
            int end = i;
            while (end < word.Length && char.IsAsciiLetterUpper(word[end])) end++;
            bool hasEnglishOnly = word[i..end].Any(IsEnglishOnlyCapital);
            if (hasEnglishOnly && (!KoreanShiftKeys.Contains(word[i]) || end - i < 2)) return null;
            if (hasEnglishOnly)
            {
                if (i == 0) afterStart = false;
                if (end < word.Length && !reading.StartsWith(LayoutConverter.ToKorean(unshifted[..end]), StringComparison.Ordinal))
                    insideSyllable = true;
            }
            i = end - 1;
        }
        return (insideSyllable, afterStart);
    }

    // Double final consonants (ㄳ ㄵ ㄶ ㄺ ...), which no anonymous initial is.
    static bool IsCluster(char jamo) => "ㄳㄵㄶㄺㄻㄼㄽㄾㄿㅀㅄ".Contains(jamo);

    // English mode: everyday syllables only (a rare one there is more likely English keys). Korean mode: Korean
    // typed with a slip still reads as Korean (신ㄱ노, 반성ㄴ), and so does a lone consonant before syllables, the
    // way news writes anonymous things (ㄱ자형); English typed there is mostly lone jamo (ㅎㄱ메ㅗㅛ, ㅅ체퍋ㅈ).
    // Korean mode: the syllables of a reading, lone jamo left out, make a Korean word (신ㄱ노 → 신노 no, 반성ㄴ → 반성
    // yes); ㅍ둣미 (vental) makes none.
    bool IsKoreanInSyllables(string korean) =>
        new string(korean.Where(Jamo.IsSyllable).ToArray()) is { Length: > 0 } syllables && (_korean(syllables) > 0 || IsKoreanWord(syllables));

    // Chat in Korean mode reads as Korean too: a jamo doubled (ㅋㅋ손톱, ㅇㅇ대교, ㅍㅍㅅㅅ), and two characters are too
    // few to tell (영ㅈ, 앞ㄹ).
    static bool IsReadableKorean(string korean, Layout typedIn) =>
        typedIn == Layout.Korean
            ? korean.Length > 0 && (korean.Count(Jamo.IsJamo) < korean.Count(Jamo.IsSyllable)
                || (korean.Length >= 2 && Jamo.IsConsonant(korean[0]) && korean[1..].All(Jamo.IsSyllable))
                || korean.Length <= 2
                || Enumerable.Range(1, korean.Length - 1).Any(i => Jamo.IsJamo(korean[i]) && korean[i] == korean[i - 1]))
            : IsWellFormed(korean);

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
    // 하겠습니다 is five syllables.
    static readonly int LongestParticle = Particles.Max(p => p.Length);

    /// <summary>
    /// A word that mixes an English word with a Korean particle, typed in one mode: "shortagedlek" in English
    /// mode or "놳ㅁㅎㄷ이다" in Korean mode both become "shortage이다". Only tried when the whole word is not
    /// already a real word. <paramref name="keys"/> are the keys as typed, capitals included.
    /// </summary>
    public string? CheckMixed(string keys, Layout typedIn)
    {
        // A sign or currency in front of a number stays as it is: -6qhek → -6보다, $5fmf → $5를.
        if (typedIn == Layout.English && keys.Length > 1 && NumberSigns.Contains(keys[0]) && char.IsAsciiDigit(keys[1]))
            return CheckMixed(keys[1..], typedIn) is { } signed ? keys[0] + signed : null;
        // Digits only in English mode (ch4가, mp3파일, 6보다): in Korean mode 초4가 is real Korean ("4th grader").
        if (keys.Length < 3 || !keys.All(c => char.IsAsciiLetter(c) || (typedIn == Layout.English && char.IsAsciiDigit(c)))) return null;
        if (typedIn == Layout.English && IsKnownName(keys) && !IsKoreanWithShift(keys)) return null;
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
        bool isOnlySingleLetter = isRealWord && typedIn == Layout.English
            && ((wholeScore < 2.0f && (char.IsAsciiLetterLower(keys[0]) || (keys.Length >= 4 && IsParticleChain(afterFirst))))
                // A capital and a group syllable (B조, A급) beat a rare abbreviation (bwh 2.6).
                || (wholeScore < 3.0f && IsEnglishOnlyCapital(keys[0]) && CapitalSuffixes.Contains(afterFirst)));
        if (isRealWord && !isOnlySingleLetter) return null;

        // Longest English prefix first. A code (mp3, ch4) ends at its last digit, so there the shortest prefix
        // that includes it goes first: mp3 + 파일, not mp3vk + 일.
        // One capital followed by lowercase (A씨는, K리그) is a common Korean way of writing and goes first,
        // so A씨는 does not become Atl + 는.
        int lastDigit = keys.LastIndexOfAny("0123456789".ToCharArray());
        // Where the last camelCase part starts (the N of versusNodejs), or 0.
        int lastInnerCapital = Enumerable.Range(1, Math.Max(0, keys.Length - 1))
            .LastOrDefault(i => char.IsAsciiLetterLower(keys[i - 1]) && IsEnglishOnlyCapital(keys[i]));
        bool isCapitalThenLower = IsEnglishOnlyCapital(keys[0]) && char.IsAsciiLetter(keys[1]) && !IsEnglishOnlyCapital(keys[1]);
        var splits = (lastDigit >= 0
            ? Enumerable.Range(lastDigit + 1, keys.Length - lastDigit - 1)
            : isCapitalThenLower
                ? Enumerable.Range(2, keys.Length - 2).Reverse().Prepend(1)
                : Enumerable.Range(2, keys.Length - 2).Reverse().Append(1)).ToList();   // a lowercase single letter last (a는)

        // Splits with solid evidence first; a prefix no word list knows (tcpview) only when none of them fits,
        // so CEO들의 is not taken as CEOemf + 의.
        // For a word no list has, the longest particle chain goes first: Chihuahuita + 이던, not Chihuahuitadl + 던.
        // A name on the list before a long particle comes first of all: Dobbek에게서, even though dobb is a word too.
        // When a name and the same name + dl both test positive, the 이 goes with the ending (Freu이라며, not
        // Freudl + 라며): names seldom end in dl.
        var nameSplits = splits.Where(i => i >= 4 && IsKnownName(keys[..i])).ToList();
        string? nameSplit = nameSplits.OrderBy(i => keys[..i].EndsWith("dl") && nameSplits.Contains(i - 2) ? 1 : 0)
            .Select(i => TrySplits(allowUnknownEnglish: true, [i], isName: true)).FirstOrDefault(r => r is not null);
        return nameSplit ?? TrySplits(allowUnknownEnglish: false) ?? BestUnknownSplit();

        // Several splits may fit a word no list has. A prefix on the name list goes first (Dobbek + 에게서, not
        // Dobb + 다에게서), then the more common Korean ending.
        string? BestUnknownSplit() => splits.Where(i => i > 1)
            .Select(i => (Split: i, Result: TrySplits(allowUnknownEnglish: true, [i])))
            .Where(c => c.Result is not null)
            .OrderByDescending(c => IsKnownName(keys[..c.Split]))
            .ThenByDescending(c => _korean(c.Result![c.Split..]))
            .ThenBy(c => c.Split)
            .Select(c => c.Result)
            .FirstOrDefault();

        // A shorter English word, clearly more common, before a particle chain of the remaining keys.
        bool HasBetterShorterSplit(int split)
        {
            float needed = Math.Max(_o.KnownEnglish, _english(keys[..split]) + 1.0f);
            return Enumerable.Range(2, Math.Max(0, split - 2)).Any(j => _english(keys[..j]) >= needed
                && keys[j..].All(char.IsAsciiLetter) && !HasEnglishOnlyShift(keys[j..]) && IsParticleChain(LayoutConverter.ToKorean(keys[j..])));
        }

        string? TrySplits(bool allowUnknownEnglish, List<int>? order = null, bool isName = false)
        {
            foreach (int split in order ?? splits)
            {
                string prefix = keys[..split], rest = keys[split..];
                // A single letter: a capital (A씨는), or lowercase before a long particle or a word (a는, e스포츠) —
                // "a" + 가 or 해 alone is too ambiguous. A single digit is a number (6보다).
                bool isLowerLetter = prefix.Length == 1 && char.IsAsciiLetter(prefix[0]) && !IsEnglishOnlyCapital(prefix[0]);
                if (isOnlySingleLetter && prefix.Length != 1) continue;
                // A listed word stays whole before a common word: genro is not g + 두개 (a는 still splits). Before a
                // particle too, unless the letter is a word itself: csms is not c + 는, pehla not p + 됨.
                if (isOnlySingleLetter && char.IsAsciiLetterLower(prefix[0])
                    && (!IsParticleChain(LayoutConverter.ToKorean(rest)) || _english(prefix) <= 0)) continue;
                // Not inside a camelCase part that is a common word itself: versusNodejs is not versusNod + 던 (Nodejs
                // 6.5); pH가, QnA로 still split (Hrk 2.0, Afh 1.5).
                if (typedIn == Layout.English && lastInnerCapital > 0 && split > lastInnerCapital
                    && _english(keys[lastInnerCapital..]) >= _o.MixedEnglishPrefix) continue;
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
                // Any complete syllable counts here, the rarer ones too (스쾃과, 험상궃은, 네트웤의).
                // Rarer syllables (둫디, 쾃) count only when no English word is behind them: engel곡선, not tmzhkt + 과.
                // ㅇㅇ (a placeholder name) and ㅋㅋ too (ㅇㅇ에, ㅋㅋ가), though dd is listed English; other repeats are
                // English letters more often (QQ뮤직, gg를, cc를, mm로, PP를).
                bool isKoreanPrefix = IsWellFormed(prefixKorean) || (prefixKorean.All(Jamo.IsSyllable) && _english(prefix) < _o.KnownEnglish)
                    || (prefixKorean.Length >= 2 && prefixKorean[0] is 'ㅇ' or 'ㅋ' && prefixKorean.All(c => c == prefixKorean[0]));
                if (typedIn == Layout.Korean && !HasEnglishOnlyShift(prefix) && isKoreanPrefix
                    && !IsEnglishBeforeParticles(prefix, prefixKorean, LayoutConverter.ToKorean(rest), isKoreanMode: true)) continue;
                // Letters with digits (ch4 = chapter 4, mp3, h2o) are codes, never Korean typing; no word list has them.
                // Short only: a long letters+digits run before "rk" is more likely a password-like string.
                bool isCode = prefix.Length <= 6 && prefix.Any(char.IsAsciiDigit) && prefix.Any(char.IsAsciiLetter);
                // A number and a word or unit after it, however long: 512byte이기, 100MB를, 30minutes에.
                int numberLength = prefix.TakeWhile(char.IsAsciiDigit).Count();
                isCode |= numberLength > 0 && numberLength < prefix.Length && prefix[numberLength..].All(char.IsAsciiLetter)
                    && (_english(prefix[numberLength..]) >= _o.KnownEnglish || NumberSuffixes.Contains(prefix[numberLength..].ToLowerInvariant()));
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
                // A capitalized word before a two-key particle is too often a name (Dikeh, not Dik + 도): it needs a
                // common word there, as the other particles do.
                bool isNameLike = typedIn == Layout.English && IsCapitalized(keys) && !isLongParticle;
                // Three lowercase letters or more without a vowel are an abbreviation, never the head of a name: std (3.1)
                // + 가, dll + 만. Two are too often the start of a longer one (jndi is not jn + 야).
                bool isAbbreviation = isParticle && prefix.Length >= 3 && prefix.All(c => char.IsAsciiLetterLower(c) && !"aeiouy".Contains(c));
                // A long lowercase word of eight letters or more takes any listed word (stereocilia 1.9 + 가).
                float needed = typedIn == Layout.Korean || (isSafeParticle && !isNameLike) || isAbbreviation ? _o.KnownEnglish
                    : isNameLike && isSafeParticle ? 3.0f
                    : korean == "가" && prefix.Length >= 8 && prefix.All(char.IsAsciiLetterLower) ? _o.KnownEnglish
                    : korean == "가" && prefix.Length >= 4 ? 2.5f
                    : _o.MixedEnglishPrefix;
                // A lowercase word that cannot be read as Korean at all is English even if no list has it
                // (tcpview, Jagdtiger), when a long particle follows.
                // ...or that starts with Shift on a key without a shifted jamo (Altmejd reads 미스덩, but A is English).
                // A listed name takes 이 에 도 로 as well (Dorda도, Amoke에).
                bool isUnknownEnglish = allowUnknownEnglish && (isLongParticle || (isName && isSafeParticle)) && prefix.Length > 1
                    && prefix[1..].All(char.IsAsciiLetterLower)
                    && (isName || !IsReadableKorean(LayoutConverter.ToKorean(prefix), typedIn) || IsEnglishOnlyCapital(prefix[0]));
                if (isLowerLetter && !(isLongParticle || (korean.Length >= 2 && IsWellFormed(korean) && _korean(korean) >= _o.MixedKoreanRest))) continue;
                // A long word whose keys make no Korean at all, before 이 에 도 로: listed however rarely (microphonic이,
                // 1.0), or shaped like English (vental도: every three letters occur in listed words), which random
                // keys seldom are.
                // Longer endings have their own rule (email + 이라는건, not emaildl + 라는건).
                bool isRareLongWord = korean is "이" or "에" or "도" or "로" && prefix.Length >= 6 && prefix.All(char.IsAsciiLetterLower)
                    && (_english(prefix) > 0 || _looksEnglish(prefix))
                    && (!IsReadableKorean(prefixKorean, typedIn) || (typedIn == Layout.Korean && !IsKoreanInSyllables(prefixKorean)));
                bool isVouched = isAcronym || isCode || (isDeliberate && isSafeParticle) || isUnknownEnglish || isLowerLetter || isRareLongWord;
                if (!isVouched && _english(prefix) < needed) continue;
                // In English mode the rest must not be common English itself: Upwork is not Up + 재가 ("work"),
                // while abbreviations and slang (wh in A조, gud in B형) do not count.
                bool isRestEnglish = typedIn == Layout.English && _english(rest) >= 4.0f;
                // A common word of two or more syllables (engel곡선, LG전자), or after an acronym also one syllable
                // (B형, FA컵, PC방); a lone syllable after a plain word is too easily the tail of a name (Durango → Duran해).
                bool isCommonWord = IsWellFormed(korean) && !isRestEnglish
                    && (korean.Length >= 2 ? _korean(korean) >= (isAcronym ? 3.0f : _o.MixedKoreanRest)
                        // after a single capital only a full syllable of three keys (B형, B팀), not Gwo → G재
                        : isAcronym && (prefix.Length >= 2 || rest.Length >= 3 || CapitalSuffixes.Contains(korean)) && _korean(korean) >= 3.0f)
                    // After an acronym also a rarer word + particle: SK텔레콤은, LG유플러스는 (텔레콤 2.6).
                    || (isAcronym && prefix.Length >= 2 && (IsWordAndParticles(korean) || (korean.Length >= 2 && IsWellFormed(korean) && _korean(korean) >= 2.5f)))
                    // A capital and a group syllable + particles: A조에는, B팀이.
                    || (isAcronym && korean.Length >= 2 && IsWellFormed(korean) && CapitalSuffixes.Contains(korean[..1]) && IsParticleChain(korean[1..]));
                // Per unit: kg당, km당, t당.
                bool isPerUnit = korean == "당" && NumberSuffixes.Contains(prefix.ToLowerInvariant());
                // A one-syllable particle may be the tail of a longer ending after a more common word: radio + 해도,
                // not radiogo + 도; precision + 야말로, not precisiondiakf + 로. That split comes later in the loop.
                if (isParticle && korean.Length == 1 && HasBetterShorterSplit(split)) continue;
                // A capitalized word read as a short English part + a Korean word is a name (Elsken is not El + 나두).
                if (isCommonWord && !isParticle && !isAcronym && typedIn == Layout.English && IsCapitalized(keys) && prefix.Length < 4) continue;
                if (isParticle || isCommonWord || isPerUnit) return prefix + korean;
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

    /// <summary>English-mode keys of a particle typed after a space (rk → 가), which as English are rarer (not di, au).</summary>
    public static bool IsParticleAfterSpace(string keys) =>
        IsParticleChain(LayoutConverter.ToKorean(keys)) && WordFrequency.English.Value.Score(keys) < 4.0f;

    /// <summary>One to three particles/endings in a row: 에서는 = 에서 + 는.</summary>
    public static bool IsParticleChain(string text, int depth = 0)
    {
        if (text.Length == 0) return depth > 0;
        if (depth == 3) return false;
        for (int length = Math.Min(LongestParticle, text.Length); length >= 1; length--)
        {
            if (Particles.Contains(text[..length]) && IsParticleChain(text[length..], depth + 1)) return true;
        }
        return false;
    }

    /// <summary>Only complete, everyday syllables (no stray jamo, nothing outside KS X 1001).</summary>
    public static bool IsWellFormed(string korean) =>
        korean.Length > 0 && korean.All(c => Jamo.IsSyllable(c) && CommonSyllables.Contains(c));
}

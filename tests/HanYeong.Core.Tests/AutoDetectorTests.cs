using HanYeong.Core;

namespace HanYeong.Core.Tests;

/// <summary>Decision rules with fixed scores, independent of the shipped data.</summary>
public class AutoDetectorRuleTests
{
    static readonly Dictionary<string, float> En = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hello"] = 5.0f, ["and"] = 7.0f, ["so"] = 6.6f, ["dl"] = 2.0f, ["zzz"] = 2.0f,
    };

    static readonly Dictionary<string, float> Ko = new() { ["안녕"] = 5.0f, ["이"] = 6.0f, ["내"] = 6.3f, ["뭉"] = 1.8f };

    readonly AutoDetector _detector = new(w => En.GetValueOrDefault(w), w => Ko.GetValueOrDefault(w), w => w == "rkdl");

    [Theory]
    [InlineData("dkssud", "안녕")]       // unknown English, common Korean
    [InlineData("dl", "이")]             // rare English, very common Korean
    [InlineData("tkfkdgo", "사랑해")]    // unseen but well-formed Korean
    [InlineData("Rk", "까")]             // Shift letters are Korean keys
    public void EnglishModeConverts(string typed, string expected) => Assert.Equal(expected, _detector.Check(typed, Layout.English));

    [Theory]
    [InlineData("so")]          // 내: both common
    [InlineData("hello")]       // not valid Hangul
    [InlineData("Hello")]       // capital on a non-shift key: a real English word
    [InlineData("abc1")]        // digits
    [InlineData("a")]           // too short
    [InlineData("rkdl")]        // exception word
    public void EnglishModeLeavesAlone(string typed) => Assert.Null(_detector.Check(typed, Layout.English));

    [Theory]
    [InlineData("ㅗ디ㅣㅐ", "hello")]   // broken jamo, known English
    [InlineData("뭉", "and")]          // rare Korean, very common English
    public void KoreanModeConverts(string typed, string expected) => Assert.Equal(expected, _detector.Check(typed, Layout.Korean));

    [Theory]
    [InlineData("내")]        // common Korean
    [InlineData("ㅋㅋㅋ")]     // chat (would be "zzz")
    [InlineData("햐솧")]       // unknown English
    public void KoreanModeLeavesAlone(string typed) => Assert.Null(_detector.Check(typed, Layout.Korean));

    [Fact]
    public void CommonSyllablesAreKsX1001()
    {
        Assert.Equal(2350, CommonSyllables.Count);
        Assert.False(CommonSyllables.Contains('뷁'));
    }
}

/// <summary>Realistic cases against the shipped frequency tables.</summary>
public class AutoDetectorDataTests
{
    static readonly AutoDetector Detector = new();

    [Theory]
    [InlineData("dkssudgktpdy", "안녕하세요")]
    [InlineData("rmfo", "그래")]
    [InlineData("ㅗ디ㅣㅐ", "hello")]
    [InlineData("뭉", "and")]
    [InlineData("햐소ㅕㅠ", "github")]
    public void Converts(string typed, string expected) =>
        Assert.Equal(expected, Detector.Check(typed, LayoutConverter.Detect(typed)));

    [Theory]
    [InlineData("so")]
    [InlineData("sms")]     // hand-picked words are never turned into Korean
    [InlineData("lol")]
    [InlineData("내")]
    [InlineData("ㅋㅋㅋ")]
    [InlineData("넵")]
    public void LeavesAlone(string typed) => Assert.Null(Detector.Check(typed, LayoutConverter.Detect(typed)));
}

/// <summary>English word + Korean particle or word, typed in one mode.</summary>
public class MixedWordTests
{
    static readonly AutoDetector Detector = new();

    [Theory]
    [InlineData("shortagedlek", "shortage이다")]
    [InlineData("updategoTek", "update했다")]
    [InlineData("SNSfmf", "SNS를")]          // acronym missing from the word lists
    [InlineData("LArk", "LA가")]             // regression: looked up as "lark"
    [InlineData("giffendms", "giffen은")]    // user's example: rare word, three-key particle
    [InlineData("ch4rk", "ch4가")]           // user's example (chapter 4): letters + digits
    [InlineData("mp3vkdlf", "mp3파일")]
    [InlineData("-6qhek", "-6보다")]          // user's example: number + particle, sign kept
    [InlineData("+2akszma", "+2만큼")]        // user's example: number + 만큼
    [InlineData("rebaterk", "rebate가")]       // user's example: rarer word + 가
    [InlineData("fMRIsms", "fMRI는")]          // user's example: acronym starting lowercase
    [InlineData("3ro", "3개")]               // number + counter
    [InlineData("2024susdp", "2024년에")]
    [InlineData("ATlsms", "A씨는")]          // single capital letter (씨 is Shift+T, l)
    [InlineData("1ckwjsdptj", "1차전에서")]    // number + ordinary word
    [InlineData("6wkghleka", "6자회담")]
    [InlineData("Bgud", "B형")]              // capital + one syllable
    [InlineData("FAzjq", "FA컵")]
    [InlineData("CEOemfdml", "CEO들의")]      // regression: not CEOemf + 의
    [InlineData("tcpviewfmf", "tcpview를")]  // unknown English, long particle
    [InlineData("Lancetdp", "Lancet에")]      // rarer English, safe two-key particle
    [InlineData("OpenGLdl", "OpenGL이")]      // inner capitals
    [InlineData("asms", "a는")]              // user's example: lowercase letter + long particle
    [InlineData("etmvhcm", "e스포츠")]
    public void EnglishMode(string keys, string expected) => Assert.Equal(expected, Detector.CheckMixed(keys, Layout.English));

    [Theory]
    [InlineData("shortagedlek", "shortage이다")]
    [InlineData("SNSdp", "SNS에")]           // shows as the real word 눈에; Shift on S/N means English
    [InlineData("engelrhrtjs", "engel곡선")] // user's example 둫디곡선: English + Korean noun
    public void KoreanMode(string keys, string expected) => Assert.Equal(expected, Detector.CheckMixed(keys, Layout.Korean));

    [Theory]
    [InlineData("work", Layout.English)]          // a real English word
    [InlineData("Namadi", Layout.English)]        // a name
    [InlineData("Novak", Layout.English)]         // not Nov + 만
    [InlineData("Han", Layout.English)]           // not H + 무 ("an" is English)
    [InlineData("isms", Layout.English)]          // a real word, not i + 는
    [InlineData("BioLargo", Layout.English)]      // regression: inner capital + 해 (go)
    [InlineData("7Bravo", Layout.English)]        // regression: code + one syllable
    [InlineData("Gwo", Layout.English)]           // regression: a name, not G + 재
    [InlineData("CyberArk", Layout.English)]      // CamelCase, not CyberA + 가
    [InlineData("Drua", Layout.English)]          // capitalized rare word, not D + 겸
    [InlineData("Ndidi", Layout.English)]         // on the name list, not N + 야야
    [InlineData("PowerPoint", Layout.English)]
    [InlineData("rmEOsms", Layout.Korean)]        // regression: Korean typo 그떄는
    [InlineData("INGKA", Layout.English)]         // capital particle keys
    [InlineData("Upwork", Layout.English)]        // regression: rest "work" read as 재가
    [InlineData("cnrkehlqslek", Layout.Korean)]   // regression: 추가됩니다 was split into cn + 가됩니다
    [InlineData("chrk", Layout.Korean)]           // digits are English-mode only (초4가 is Korean)
    [InlineData("user123", Layout.English)]
    [InlineData("3rd", Layout.English)]           // English ordinals and units stay
    [InlineData("4th", Layout.English)]
    [InlineData("10kg", Layout.English)]
    public void LeavesAlone(string keys, Layout layout) => Assert.Null(Detector.CheckMixed(keys, layout));
}

/// <summary>The app's full decision between a whole-word reading and an English + Korean split.</summary>
public class SuggestTests
{
    [Theory]
    [InlineData("autocompact")]        // user's example (며새채ㅡㅔㅁㅊㅅ): two common words, broken Hangul
    [InlineData("keyboardshortcut")]
    public void KoreanModeCompound(string keys) =>
        Assert.Equal(keys, Detector.Suggest(LayoutConverter.ToKorean(keys), keys, Layout.Korean));

    // Found in 3.7 million more sentences (docs/accuracy-report.md, edge cases).
    [Theory]
    [InlineData("SEC")]          // acronym typed in Korean mode: Shift on S, C changes nothing in Korean
    [InlineData("DNA")]
    [InlineData("I")]
    public void KoreanModeAcronym(string keys) =>
        Assert.Equal(keys, Detector.Suggest(LayoutConverter.ToKorean(keys), keys, Layout.Korean), ignoreCase: true);

    [Theory]
    [InlineData("ㄱ씨는")]       // anonymous person in news, not r + 씨는
    [InlineData("ㄱ씨")]
    [InlineData("ㄴ양의")]
    [InlineData("ㄱ업체는")]
    [InlineData("ㅅ아파트")]
    [InlineData("갯녹음")]       // proper Korean stays even if a part reads as English (rot)
    [InlineData("해미면")]
    [InlineData("4륜")]          // Korean typed on purpose after a number
    [InlineData("1녀")]
    public void KoreanModeStays(string screen) => Assert.Null(Detector.Suggest(screen, LayoutConverter.ToEnglish(screen), Layout.Korean));

    [Theory]
    [InlineData("1tla", "1심")]
    [InlineData("3fn", "3루")]
    [InlineData("tl·eh", "시·도")]
    [InlineData("`chdlsrkwhr’", "`초인가족’")]
    [InlineData("SKxpffpzhadms", "SK텔레콤은")]
    [InlineData("Asms", "A는")]
    [InlineData("Awh", "A조")]
    public void EnglishModeEdge(string keys, string expected) => Assert.Equal(expected, Detector.Suggest(keys, keys, Layout.English));

    [Theory]
    [InlineData("5th")]
    [InlineData("10cm")]
    [InlineData("9am")]
    [InlineData("4to")]
    [InlineData("75cl")]
    [InlineData("4chan")]
    [InlineData("222Rn")]
    public void EnglishNumberSuffixStays(string keys) => Assert.Null(Detector.Suggest(keys, keys, Layout.English));

    [Fact]
    public void AddressPieces()
    {
        Assert.True(AutoDetector.IsInAddress("www."));
        Assert.True(AutoDetector.IsInAddress("naver.co."));
        Assert.True(AutoDetector.IsInAddress("me@"));
        Assert.False(AutoDetector.IsInAddress("안녕 "));
        Assert.False(AutoDetector.IsInAddress(""));
    }

    static readonly AutoDetector Detector = new();

    [Theory]
    [InlineData("quantityrk", "quantity가")]   // user's example: whole reading 벼무샤쇼가 exists nowhere
    [InlineData("dkssudgktpdy", "안녕하세요")]  // a known whole word stays whole
    [InlineData("shortagedlek", "shortage이다")]
    [InlineData("+2akszma", "+2만큼")]        // user's example: number + 만큼
    [InlineData("fMRIsms", "fMRI는")]           // user's example: lowercase letter belongs to the acronym
    [InlineData("mRNAfmf", "mRNA를")]
    public void EnglishMode(string keys, string expected) => Assert.Equal(expected, Detector.Suggest(keys, keys, Layout.English));
}

/// <summary>Sentence context: an earlier word of the sentence was already fixed the same way.</summary>
public class ContextTests
{
    static readonly AutoDetector Normal = new();
    static readonly AutoDetector InContext = new(options: AutoDetectorOptions.InContext);

    [Theory]
    [InlineData("새", "to")]     // ambiguous on its own
    [InlineData("ㅁ", "a")]      // one key
    public void KoreanModeFollowsContext(string typed, string expected)
    {
        Assert.Null(Normal.Check(typed, Layout.Korean));
        Assert.Equal(expected, InContext.Check(typed, Layout.Korean));
    }

    [Fact]
    public void ClearKoreanStays() => Assert.Null(InContext.Check("친구", Layout.Korean));

    [Fact]
    public void EnglishModeFollowsContext()
    {
        Assert.Null(Normal.Check("so", Layout.English));
        Assert.Equal("내", InContext.Check("so", Layout.English));
        Assert.Equal("해", InContext.Check("go", Layout.English));   // user's example: 분석부터 해
        // A lone jamo before a fixed word is looked back at (ㅑ ㅈ무ㅅ → I want); chat jamo stays.
        Assert.Equal("I", InContext.Suggest("ㅑ", "I", Layout.Korean), ignoreCase: true);
        Assert.Equal("a", InContext.Suggest("ㅁ", "a", Layout.Korean));
        Assert.Null(InContext.Suggest("ㅇ", "d", Layout.Korean));
        Assert.Null(InContext.Suggest("ㅋ", "z", Layout.Korean));
    }
}

/// <summary>Words joined by - / ' are judged part by part and converted as a whole.</summary>
public class JoinedWordTests
{
    static readonly AutoDetector Detector = new();

    [Theory]
    [InlineData("ㅔ걏ㄷ-채ㅜ녀ㅡㅔ샤ㅐㅜ", "price-consumption")]   // user's example
    [InlineData("애ㅜ'ㅅ", "don't")]                           // one-key part follows
    public void KoreanModeConverts(string typed, string expected) => Assert.Equal(expected, Detector.Check(typed, Layout.Korean));

    // Found by mining the test corpora: quotes, brackets, middle dots and numbers inside words.
    [Theory]
    [InlineData("“이번")]
    [InlineData("것”이라고")]
    [InlineData("시·군")]
    [InlineData("달러(약")]
    [InlineData("제2차")]
    [InlineData("7시30분")]
    [InlineData("5%로")]
    [InlineData("1분기")]
    [InlineData("65세")]
    [InlineData("양해각서(MOU)를")]   // acronym typed as meant stays
    [InlineData("유럽연합(EU)")]
    [InlineData("삼성SDS가")]         // acronym inside, no separator
    [InlineData("비타민C가")]
    [InlineData("부장판사)는")]       // particle after a closing bracket
    [InlineData("홈페이지(www")]      // English typed as meant stays
    [InlineData("주스(0")]
    [InlineData("1박2일")]
    [InlineData("100여명이")]
    [InlineData("늦을수도…")]
    [InlineData("▲드라마")]
    public void KoreanTypedInEnglishMode(string korean) =>
        Assert.Equal(korean, Detector.Check(KeysOf(korean), Layout.English));

    [Theory]
    [InlineData("it’s")]
    [InlineData("(or")]
    [InlineData("I'm")]               // contraction: stray jamo around the apostrophe
    [InlineData("you’re")]
    public void EnglishTypedInKoreanMode(string english) =>   // the app restores capitals from the typed keys
        Assert.Equal(english.ToLowerInvariant(), Detector.Check(LayoutConverter.ToKorean(english), Layout.Korean)?.ToLowerInvariant());

    static string KeysOf(string text) => string.Concat(text.Select(c => Jamo.IsHangul(c) ? LayoutConverter.ToEnglish(c.ToString()) : c.ToString()));

    [Theory]
    [InlineData("e-mail", Layout.English)]
    [InlineData("신-구", Layout.Korean)]
    [InlineData("(see", Layout.English)]      // real English inside brackets
    [InlineData("20th", Layout.English)]      // ordinals stay
    [InlineData("1980s", Layout.English)]
    [InlineData("abc1", Layout.English)]
    [InlineData("Seoul's", Layout.English)]   // English typed right, with an apostrophe
    [InlineData("McDonald", Layout.English)]  // capitals inside an English name
    [InlineData("YouTube", Layout.English)]
    [InlineData("British", Layout.English)]       // regression: capital B taken as an acronym
    [InlineData("Henry's", Layout.English)]
    [InlineData("X-Men", Layout.English)]
    [InlineData("Port-au-Prince", Layout.English)] // regression: au → 며 between kept English
    [InlineData("pa$$rk", Layout.English)]         // password-like symbols
    [InlineData("McVeigh's", Layout.English)]      // regression: unknown Korean next to kept English
    [InlineData("Third-Worldism", Layout.English)]
    [InlineData("rlaalswn12", Layout.English)] // an ID: 김민주 typed as letters, then digits
    public void LeavesAlone(string typed, Layout layout) => Assert.Null(Detector.Check(typed, layout));
}

/// <summary>English + Korean typed entirely in Korean mode; the keys carry the Shift presses.</summary>
public class KoreanModeMixedTests
{
    static readonly AutoDetector Detector = new();

    [Theory]
    [InlineData("A씨")]
    [InlineData("KT가")]
    [InlineData("LG전자는")]
    [InlineData("SK텔레콤")]
    [InlineData("ESPN은")]
    [InlineData("e스포츠")]
    [InlineData("suffix라던가")]    // user's example: 녈럍라던가 (녈럍 is no Korean word)
    public void Converts(string word)
    {
        string keys = LayoutConverter.ToEnglish(word);
        Assert.Equal(word, Detector.Suggest(LayoutConverter.ToKorean(keys), keys, Layout.Korean));
    }
}

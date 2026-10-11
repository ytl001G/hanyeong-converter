using HanYeong.Core;

namespace HanYeong.Core.Tests;

/// <summary>Rules with fixed scores, independent of the shipped data.</summary>
public class AutoDetectorRuleTests
{
    static readonly Dictionary<string, float> En = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hello"] = 5.0f, ["and"] = 7.0f, ["so"] = 6.6f, ["the"] = 7.3f, ["for"] = 6.8f,
        ["dl"] = 2.0f, ["zzz"] = 2.0f, ["gg"] = 3.0f,
    };

    static readonly Dictionary<string, float> Ko = new()
    {
        ["안녕"] = 5.0f, ["안녕하세요"] = 5.2f, ["이"] = 6.0f, ["내"] = 6.3f, ["가"] = 5.5f,
        ["뭉"] = 1.8f, ["랙"] = 1.2f,
    };

    readonly AutoDetector _detector = new(
        w => En.GetValueOrDefault(w),
        w => Ko.GetValueOrDefault(w),
        w => w == "dkssudgktpdy2");

    [Theory]
    [InlineData("dkssud", "안녕")]              // unknown English, common Korean
    [InlineData("dkssudgktpdy", "안녕하세요")]
    [InlineData("dl", "이")]                    // rare English, very common Korean
    [InlineData("rk", "가")]
    [InlineData("tkfkdgo", "사랑해")]            // unseen Korean but well-formed, unknown English
    public void EnglishModeConverts(string typed, string expected) =>
        Assert.Equal(expected, _detector.Check(typed, Layout.English));

    [Theory]
    [InlineData("hello")]        // not well-formed Korean
    [InlineData("and")]          // 뭉: English far more common
    [InlineData("so")]           // 내: both common
    [InlineData("Hello")]        // capital H is not a Korean shift key
    [InlineData("API")]
    [InlineData("a")]            // too short
    [InlineData("abc1")]         // digits
    [InlineData("user@mail")]
    public void EnglishModeLeavesAlone(string typed) =>
        Assert.Null(_detector.Check(typed, Layout.English));

    [Fact]
    public void ShiftLettersCountAsKorean() =>
        Assert.Equal("까", new AutoDetector(_ => 0, _ => 0).Check("Rk", Layout.English));

    [Theory]
    [InlineData("ㅗ디ㅣㅐ", "hello")]   // broken jamo, known English
    [InlineData("뭉", "and")]          // rare Korean, very common English
    [InlineData("랙", "for")]
    [InlineData("솓", "the")]
    public void KoreanModeConverts(string typed, string expected) =>
        Assert.Equal(expected, _detector.Check(typed, Layout.Korean));

    [Theory]
    [InlineData("내")]        // common Korean
    [InlineData("이")]
    [InlineData("ㅋㅋㅋ")]     // chat: consonants only (would be "zzz")
    [InlineData("ㅎㅎ")]       // "gg"
    [InlineData("ㅠㅠ")]
    [InlineData("안녕")]
    [InlineData("ㅁㄴㅇㄹ")]   // consonants only
    [InlineData("햐솧")]       // unknown English
    public void KoreanModeLeavesAlone(string typed) =>
        Assert.Null(_detector.Check(typed, Layout.Korean));

    [Fact]
    public void ExceptionsAreRespected() =>
        Assert.Null(_detector.Check("dkssudgktpdy2", Layout.English));
}

public class CommonSyllablesTests
{
    [Fact]
    public void HasKsX1001Set() => Assert.Equal(2350, CommonSyllables.Count);

    [Theory]
    [InlineData('가', true)]
    [InlineData('힝', true)]
    [InlineData('힣', false)]
    [InlineData('뷁', false)]
    [InlineData('똠', false)]
    public void Membership(char c, bool expected) => Assert.Equal(expected, CommonSyllables.Contains(c));
}

/// <summary>Realistic cases against the shipped frequency tables.</summary>
public class AutoDetectorDataTests
{
    static readonly AutoDetector Detector = new();

    [Theory]
    // Korean typed in English mode
    [InlineData("dkssudgktpdy", "안녕하세요")]
    [InlineData("rkatkgkqslek", "감사합니다")]
    [InlineData("qkqh", "바보")]
    [InlineData("rmfo", "그래")]
    [InlineData("wjsmsdy", "저는요")]
    [InlineData("dhsmf", "오늘")]
    [InlineData("tkfkdgo", "사랑해")]
    [InlineData("dl", "이")]
    [InlineData("rk", "가")]
    [InlineData("sp", "네")]
    // English typed in Korean mode
    [InlineData("ㅗ디ㅣㅐ", "hello")]
    [InlineData("솓", "the")]
    [InlineData("ㅑㄴ", "is")]
    [InlineData("뭉", "and")]
    [InlineData("랙", "for")]
    [InlineData("쟈소", "with")]
    [InlineData("ㅎㅐㅐㅎㅣㄷ", "google")]
    [InlineData("햐소ㅕㅠ", "github")]
    public void Converts(string typed, string expected)
    {
        var layout = LayoutConverter.Detect(typed);
        Assert.Equal(expected, Detector.Check(typed, layout));
    }

    [Theory]
    // Real English left alone
    [InlineData("hello")]
    [InlineData("so")]
    [InlineData("go")]
    [InlineData("ok")]
    [InlineData("sms")]
    [InlineData("github")]
    [InlineData("lol")]
    [InlineData("gonna")]
    [InlineData("youtube")]
    [InlineData("seoul")]
    // Real Korean left alone
    [InlineData("내")]
    [InlineData("해")]
    [InlineData("안녕")]
    [InlineData("ㅋㅋㅋ")]
    [InlineData("ㅎㅎ")]
    [InlineData("ㅠㅠ")]
    [InlineData("그래서")]
    [InlineData("넵")]
    public void LeavesAlone(string typed)
    {
        var layout = LayoutConverter.Detect(typed);
        Assert.Null(Detector.Check(typed, layout));
    }
}

/// <summary>English word + Korean particle typed in one mode.</summary>
public class MixedWordTests
{
    static readonly AutoDetector Detector = new();

    [Theory]
    [InlineData("shortagedlek", "shortage이다")]       // particle typed in English mode
    [InlineData("updategoTek", "update했다")]
    [InlineData("githubdptjsms", "github에서는")]
    [InlineData("APIfmf", "API를")]
    [InlineData("serverrk", "server가")]
    [InlineData("emaildmf", "email을")]
    [InlineData("SNSfmf", "SNS를")]                    // acronym missing from the word lists
    [InlineData("FTArk", "FTA가")]
    [InlineData("UNdp", "UN에")]
    [InlineData("LArk", "LA가")]
    public void EnglishMode(string keys, string expected) =>
        Assert.Equal(expected, Detector.CheckMixed(keys, Layout.English));

    [Theory]
    [InlineData("shortagedlek", "shortage이다")]       // English part typed in Korean mode
    [InlineData("Githubdp", "Github에")]
    [InlineData("updategkrh", "update하고")]
    [InlineData("SNSdp", "SNS에")]
    public void KoreanMode(string keys, string expected) =>
        Assert.Equal(expected, Detector.CheckMixed(keys, Layout.Korean));

    [Theory]
    [InlineData("work", Layout.English)]        // a real English word
    [InlineData("hello", Layout.English)]
    [InlineData("seems", Layout.English)]
    [InlineData("speak", Layout.English)]
    [InlineData("gkrrysdp", Layout.Korean)]     // 학교에: real Korean
    [InlineData("dkssudgktpdy", Layout.Korean)] // 안녕하세요
    [InlineData("qwerty", Layout.English)]
    [InlineData("cnrkehlqslek", Layout.Korean)] // 추가됩니다: proper syllables in front
    [InlineData("skagksdms", Layout.Korean)]    // 남한은
    [InlineData("INGKA", Layout.English)]       // capital particle keys
    [InlineData("Namadi", Layout.English)]      // a name
    public void LeavesAlone(string keys, Layout layout) =>
        Assert.Null(Detector.CheckMixed(keys, layout));
}

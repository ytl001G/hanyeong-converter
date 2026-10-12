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

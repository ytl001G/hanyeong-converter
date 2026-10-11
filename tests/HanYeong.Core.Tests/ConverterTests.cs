using HanYeong.Core;

namespace HanYeong.Core.Tests;

public class ComposerTests
{
    [Theory]
    [InlineData("dkssud", "안녕")]
    [InlineData("dkssudgktpdy", "안녕하세요")]
    [InlineData("gksrmf", "한글")]
    [InlineData("qnfrhrl", "불고기")]
    [InlineData("rk", "가")]
    [InlineData("rkr", "각")]
    [InlineData("rkrk", "가가")]
    [InlineData("ekfr", "닭")]
    [InlineData("ekfrdl", "닭이")]
    [InlineData("ekfrk", "달가")]
    [InlineData("rkqt", "값")]
    [InlineData("rkqtdl", "값이")]
    [InlineData("rkqtk", "갑사")]
    [InlineData("hk", "ㅘ")]
    [InlineData("ghk", "화")]
    [InlineData("dnjs", "원")]
    [InlineData("dml", "의")]
    [InlineData("dho", "왜")]
    [InlineData("rt", "ㄳ")]
    [InlineData("rtk", "ㄱ사")]
    [InlineData("tT", "ㅅㅆ")]
    [InlineData("Rk", "까")]
    [InlineData("ekE", "다ㄸ")]
    [InlineData("ekEk", "다따")]
    [InlineData("Dkssud", "안녕")]
    [InlineData("kk", "ㅏㅏ")]
    [InlineData("dkssud, 123!", "안녕, 123!")]
    [InlineData("", "")]
    public void Compose(string keys, string expected) =>
        Assert.Equal(expected, LayoutConverter.ToKorean(keys));

    [Fact]
    public void BackspaceRemovesOneJamoWhileComposing()
    {
        var c = new HangulComposer();
        foreach (char k in "ekfr") c.Type(k);
        Assert.Equal("닭", c.Text);
        c.Backspace();
        Assert.Equal("달", c.Text);
        c.Backspace();
        Assert.Equal("다", c.Text);
        c.Backspace();
        Assert.Equal("ㄷ", c.Text);
        c.Backspace();
        Assert.Equal("", c.Text);
    }

    [Fact]
    public void BackspaceAfterCommitRemovesWholeSyllable()
    {
        var c = new HangulComposer();
        foreach (char k in "dkssud ") c.Type(k);
        c.Backspace();
        Assert.Equal("안녕", c.Text);
        c.Backspace();
        Assert.Equal("안", c.Text);
    }

    [Fact]
    public void BackspaceAfterMovedConsonantKeepsIt()
    {
        var c = new HangulComposer();
        foreach (char k in "rkrk") c.Type(k);
        c.Backspace();
        Assert.Equal("가ㄱ", c.Text);
    }
}

public class DecomposerTests
{
    [Theory]
    [InlineData("안녕", "dkssud")]
    [InlineData("ㅗ디ㅣㅐ", "hello")]
    [InlineData("닭", "ekfr")]
    [InlineData("까", "Rk")]
    [InlineData("의", "dml")]
    [InlineData("ㄳ", "rt")]
    [InlineData("ㅘ", "hk")]
    [InlineData("안녕, 123!", "dkssud, 123!")]
    public void ToKeys(string text, string expected) =>
        Assert.Equal(expected, LayoutConverter.ToEnglish(text));

    [Theory]
    [InlineData("안녕하세요")]
    [InlineData("닭이 값을 왜 의논해")]
    [InlineData("까치 뛰어 쌌다")]
    public void RoundTrip(string text) =>
        Assert.Equal(text, LayoutConverter.ToKorean(LayoutConverter.ToEnglish(text)));
}

public class FlipTests
{
    [Theory]
    [InlineData("dkssud", "안녕")]
    [InlineData("ㅗ디ㅣㅐ", "hello")]
    [InlineData("tkfkdgo wnjdy", "사랑해 줘요")]
    public void Flip(string text, string expected) =>
        Assert.Equal(expected, LayoutConverter.Flip(text));
}

public class TypedSegmentTests
{
    static TypedSegment Typed(Layout layout, string keys, bool capsLock = false)
    {
        var segment = new TypedSegment(layout);
        foreach (char k in keys)
        {
            if (k == '\b') segment.Backspace();
            else segment.Type(k, capsLock);
        }
        return segment;
    }

    [Fact]
    public void EnglishConvertsToKorean()
    {
        var s = Typed(Layout.English, "dkssud");
        Assert.Equal("dkssud", s.Text);
        Assert.Equal("안녕", s.Converted);
    }

    [Fact]
    public void KoreanTracksImeOutputIncludingBackspace()
    {
        // Matches the real IME: 안녕하 → ⌫ → 안녕ㅎ → ㄹ → 안녕ㅎㄹ
        var s = Typed(Layout.Korean, "dkssudgk\bf");
        Assert.Equal("안녕ㅎㄹ", s.Text);
        Assert.Equal("dkssudgf", s.Converted);
    }

    [Fact]
    public void InsertedTextThenTypingContinues()
    {
        var s = new TypedSegment(Layout.Korean, "안녕");
        foreach (char k in "gktpdy") s.Type(k);
        Assert.Equal("안녕하세요", s.Text);
        s.Backspace();
        s.Backspace();
        s.Backspace();
        Assert.Equal("안녕하", s.Text);
    }

    [Fact]
    public void CapsLockInKoreanModeTypesLatin()
    {
        var s = Typed(Layout.Korean, "AB", capsLock: true);
        Assert.Equal("AB", s.Text);
    }
}

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

public class ImeEditTests
{
    static TypedSegment Korean(string keys)
    {
        var s = new TypedSegment(Layout.Korean);
        foreach (char k in keys)
        {
            if (k == '\b') s.Backspace();
            else s.Type(k);
        }
        return s;
    }

    [Theory]
    [InlineData("hi", 2, 2)]          // ㅗ + composing ㅑ (1 step)
    [InlineData("dkssud", 2, 4)]      // 안 + composing 녕 (ㄴ ㅕ ㅇ)
    [InlineData("ekfr", 1, 4)]        // composing 닭 (ㄷ ㅏ ㄹ ㄺ)
    [InlineData("rkrk", 2, 3)]        // 가 + composing 가 that started from a carried ㄱ
    [InlineData("dkssud ", 3, 3)]     // nothing composing after Space
    [InlineData("dkssud", 1, 3)]      // only the composing syllable
    public void BackspacesCoverComposingJamo(string keys, int tail, int expected) =>
        Assert.Equal(expected, Korean(keys).BackspacesFor(tail));

    [Fact]
    public void EnglishNeedsOneBackspacePerChar()
    {
        var s = new TypedSegment(Layout.English);
        foreach (char k in "dkssud") s.Type(k);
        Assert.Equal(6, s.BackspacesFor(6));
    }

    [Theory]
    [InlineData("Hello", "ㅗ디ㅣㅐ", "Hello")]
    [InlineData("HELLO", "ㅗ띠ㅣㅒ", "HELLO")]     // Shift+E is ㄸ, Shift+O is ㅒ
    [InlineData("iPhone", "ㅑㅖㅙㅜㄷ", "iPhone")]
    [InlineData("Qwer", "ㅃㅈㄷㄱ", "Qwer")]
    public void ConvertedKeepsTypedCapitals(string keys, string screen, string expected)
    {
        var s = Korean(keys);
        Assert.Equal(screen, s.Text);
        Assert.Equal(expected, s.Converted);
    }

    [Fact]
    public void CapitalsSurviveBackspace()
    {
        var s = Korean("Hellp\bo");
        Assert.Equal("Hello", s.Converted);
    }

    [Fact]
    public void RestoreCaseForLastWord()
    {
        var s = Korean("say Hello");
        Assert.Equal("Hello", s.RestoreCase("hello"));
    }
}

using HanYeong.Core;

namespace HanYeong.Core.Tests;

public class ComposerTests
{
    [Theory]
    [InlineData("dkssud", "안녕")]
    [InlineData("ekfr", "닭")]              // double final consonant
    [InlineData("ekfrk", "달가")]           // ...whose second half moves on before a vowel
    [InlineData("rkrk", "가가")]            // single final moves on
    [InlineData("ghk", "화")]               // compound vowel
    [InlineData("rtk", "ㄱ사")]             // lone cluster splits
    [InlineData("Rk", "까")]                // Shift jamo
    [InlineData("kk", "ㅏㅏ")]              // vowels without a consonant stay apart
    [InlineData("dkssud, 123!", "안녕, 123!")]
    public void Compose(string keys, string expected) => Assert.Equal(expected, LayoutConverter.ToKorean(keys));

    // Matches the Windows IME: Backspace removes one jamo while composing, a whole syllable after commit.
    [Fact]
    public void Backspace()
    {
        var c = new HangulComposer();
        foreach (char k in "dkssud ekfr") c.Type(k);
        c.Backspace();
        Assert.Equal("안녕 달", c.Text);
        c.Backspace(); c.Backspace(); c.Backspace();
        Assert.Equal("안녕 ", c.Text);
        c.Backspace(); c.Backspace();
        Assert.Equal("안", c.Text);
    }
}

public class DecomposerTests
{
    [Theory]
    [InlineData("안녕", "dkssud")]
    [InlineData("ㅗ디ㅣㅐ", "hello")]
    [InlineData("닭", "ekfr")]
    [InlineData("까", "Rk")]
    public void ToKeys(string text, string expected) => Assert.Equal(expected, LayoutConverter.ToEnglish(text));

    [Fact]
    public void RoundTrip() =>
        Assert.Equal("닭이 값을 왜 의논해 까치 뛰어", LayoutConverter.ToKorean(LayoutConverter.ToEnglish("닭이 값을 왜 의논해 까치 뛰어")));

    [Fact]
    public void FlipDetectsDirection()
    {
        Assert.Equal("안녕", LayoutConverter.Flip("dkssud"));
        Assert.Equal("hello", LayoutConverter.Flip("ㅗ디ㅣㅐ"));
    }
}

public class TypedSegmentTests
{
    static TypedSegment Korean(string keys, bool capsLock = false)
    {
        var s = new TypedSegment(Layout.Korean);
        foreach (char k in keys)
        {
            if (k == '\b') s.Backspace();
            else s.Type(k, capsLock);
        }
        return s;
    }

    [Fact]
    public void TracksImeOutputIncludingBackspace()
    {
        // 안녕하 → ⌫ → 안녕ㅎ → ㄹ → 안녕ㅎㄹ, as the real IME does.
        var s = Korean("dkssudgk\bf");
        Assert.Equal("안녕ㅎㄹ", s.Text);
        Assert.Equal("dkssudgf", s.Converted);
    }

    [Fact]
    public void CapsLockInKoreanModeTypesLatin() => Assert.Equal("AB", Korean("AB", capsLock: true).Text);

    // Regression: toggling 한/영 to commit the composing syllable failed in Windows 11 Notepad ("hi" → "ㅗhi"),
    // so the syllable is erased jamo by jamo.
    [Theory]
    [InlineData("hi", 2, 2)]          // ㅗ + composing ㅑ
    [InlineData("dkssud", 2, 4)]      // 안 + composing 녕 (ㄴ ㅕ ㅇ)
    [InlineData("rkrk", 2, 3)]        // 가 + composing 가 that started from a carried ㄱ
    [InlineData("dkssud ", 3, 3)]     // nothing composing after Space
    public void BackspacesCoverComposingJamo(string keys, int tail, int expected) =>
        Assert.Equal(expected, Korean(keys).BackspacesFor(tail));

    [Theory]
    [InlineData("Hello", "ㅗ디ㅣㅐ", "Hello")]   // Shift on h is invisible in Korean mode
    [InlineData("HELLO", "ㅗ띠ㅣㅒ", "HELLO")]   // Shift on e/o gives ㄸ/ㅒ
    [InlineData("Hellp\bo", "ㅗ디ㅣㅐ", "Hello")] // capitals survive Backspace
    public void ConvertedKeepsTypedCapitals(string keys, string screen, string expected)
    {
        var s = Korean(keys);
        Assert.Equal(screen, s.Text);
        Assert.Equal(expected, s.Converted);
    }
}

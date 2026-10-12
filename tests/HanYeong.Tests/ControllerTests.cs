using HanYeong.Core;

namespace HanYeong.Tests;

/// <summary>
/// The whole app without a screen: keys go through the controller's hook handling, and what it lets through or
/// injects lands in a simulated text box with the Korean IME. The on-screen smoke test (tools/smoke-test.ps1) is
/// left for what only the real IME and apps can show.
/// </summary>
public class ControllerTests : IDisposable
{
    static ControllerTests()
    {
        // Settings saves on every change and the controller logs; keep the user's files out of it.
        Environment.SetEnvironmentVariable("HANYEONG_SETTINGS", Path.Combine(Path.GetTempPath(), $"hanyeong-test-{Environment.ProcessId}.json"));
        Environment.SetEnvironmentVariable("HANYEONG_LOG", Path.Combine(Path.GetTempPath(), $"hanyeong-test-{Environment.ProcessId}.log"));
    }

    readonly FakeDesktop _desktop = new();
    readonly ManualContext _context = new();
    readonly Settings _settings = new();
    readonly Controller _controller;

    public ControllerTests()
    {
        SynchronizationContext.SetSynchronizationContext(_context);
        _controller = new Controller(_settings, _desktop, hooks: false);
        _desktop.Attach(_controller);
        _controller.Ready.Wait();
    }

    public void Dispose() => _controller.Dispose();

    /// <summary>Types into a fresh text box and returns what it shows.</summary>
    internal string Type(Layout mode, string keys) => Typed(mode, keys).Box.Text;

    FakeDesktop Typed(Layout mode, string keys)
    {
        _desktop.Mode = mode;
        _desktop.Type(keys);
        return _desktop;
    }

    [Fact]
    public void HotkeyConvertsAndRevertsWithoutTouchingTheMode()
    {
        Typed(Layout.English, "dkssud").Hotkey();
        Assert.Equal("안녕", _desktop.Box.Text);
        Assert.Equal(Layout.English, _desktop.Mode);
        _desktop.Hotkey();
        Assert.Equal("dkssud", _desktop.Box.Text);
    }

    [Fact]
    public void HotkeyErasesTheComposingSyllableAndKeepsCapitals()
    {
        Typed(Layout.Korean, "Hello").Hotkey();
        Assert.Equal("Hello", _desktop.Box.Text);
    }

    [Fact]
    public void HotkeyOnComposingVowel() // reported: hi typed in Korean mode became ㅗhi
    {
        Typed(Layout.Korean, "hi").Hotkey();
        Assert.Equal("hi", _desktop.Box.Text);
    }

    [Theory]
    [InlineData(Layout.English, "dkssud.gktpdy ", "안녕.하세요 ")]
    [InlineData(Layout.Korean, "want to go ", "want to go ")]                 // sentence context: to, go
    [InlineData(Layout.Korean, "I want to go ", "I want to go ")]             // lone jamo looked back at
    [InlineData(Layout.Korean, "engelrhrtjs ", "engel곡선 ")]                 // English + Korean noun
    [InlineData(Layout.English, "dkssud gktpdy\n", "안녕 하세요\n")]          // Enter after other words
    [InlineData(Layout.English, "rlaalswn\n", "rlaalswn\n")]                  // one word + Enter: may be an ID
    [InlineData(Layout.English, "ekdma\n", "다음\n")]                         // ...unless it is a common word (chat)
    [InlineData(Layout.Korean, "fMRIsms ", "fMRI는 ")]
    [InlineData(Layout.English, "+2akszma ", "+2만큼 ")]
    [InlineData(Layout.Korean, "an apple\n", "an apple\n")]                  // user's example: 무 메ㅔㅣㄷ + Enter
    [InlineData(Layout.English, "dksehlsek\n", "안된다\n")]
    [InlineData(Layout.Korean, "so apple ", "내 apple ")]                     // 내 is as common as so: may be meant
    [InlineData(Layout.English, "www.kt.com ", "www.kt.com ")]              // address pieces stay
    [InlineData(Layout.English, "naver.co.kr ", "naver.co.kr ")]
    public void AutoMode(Layout mode, string keys, string expected)
    {
        Typed(mode, keys);
        Assert.Equal(expected, _desktop.Box.Text);
    }

    [Fact]
    public void CapitalsWithShiftHeldIntoSpace() // reported in Zoom: PTE became ㅖPTE
    {
        _desktop.Mode = Layout.Korean;
        _desktop.Down(FakeDesktop.VK_LSHIFT);
        foreach (char c in "PTE") _desktop.Tap(c);
        _desktop.Tap(FakeDesktop.VK_SPACE);
        Assert.True(_desktop.IsDown(FakeDesktop.VK_SHIFT));   // given back to the user, who still holds it
        _desktop.Up(FakeDesktop.VK_LSHIFT);
        Assert.Equal("PTE ", _desktop.Box.Text);
    }

    [Fact]
    public void TerminalGetsBackspacesAndEnterOneByOne() // Claude Code in cmd took a batch as a paste
    {
        _desktop.Exe = "cmd.exe";
        Typed(Layout.English, "dksehlsek\n");
        _context.RunUntil(() => _controller.Sending.IsCompleted);
        Assert.Equal("안된다\n", _desktop.Box.Text);
        // The replacement only: the user's own keys went through the box, not through Send.
        var replacement = _desktop.Batches.Where(b => b.Any(k => !k.IsUp)).ToList();
        Assert.All(replacement, b => Assert.True(b.All(k => k.Vk == 0) || b.Count(k => !k.IsUp) == 1));   // text, or one key
        Assert.Equal(FakeDesktop.VK_RETURN, replacement[^1].Single(k => !k.IsUp).Vk);
    }

    [Fact]
    public void KeysTypedWhileATerminalReplacementGoesOutComeAfterIt()
    {
        _desktop.Exe = "cmd.exe";
        Typed(Layout.English, "dkssud ");
        _desktop.Type("x");
        _context.RunUntil(() => _controller.Sending.IsCompleted && _context.IsIdle);
        Assert.Equal("안녕 x", _desktop.Box.Text);
    }

    [Fact]
    public void HanjaKeyAfterEnglishModeTyping() // user's idea: rh + 한자 → 고 with the Hanja list
    {
        Typed(Layout.English, "rh");
        _desktop.Tap(FakeDesktop.VK_HANJA);
        _context.RunUntil(() => _controller.Sending.IsCompleted);
        Assert.Equal(Layout.Korean, _desktop.Mode);
        Assert.Equal("고", _desktop.Box.Text);
        Assert.Equal(["고"], _desktop.HanjaRequests);
    }

    [Fact]
    public void HanjaKeyInKoreanModeIsLeftToTheIme()
    {
        Typed(Layout.Korean, "rh");
        _desktop.Tap(FakeDesktop.VK_HANJA);
        Assert.Equal(["고"], _desktop.HanjaRequests);   // the key went straight to the IME
        Assert.DoesNotContain(_desktop.Injected, k => k.Vk == FakeDesktop.VK_HANGUL);
    }

    [Fact]
    public void ArrowKeyConvertsAndStillMoves()
    {
        Typed(Layout.English, "dkssud");
        _desktop.Tap(FakeDesktop.VK_LEFT);
        _desktop.Type("x");
        Assert.Equal("안x녕", _desktop.Box.Text);
    }

    [Fact]
    public void BackspaceIsTracked()
    {
        Typed(Layout.English, "dkssuf");
        _desktop.Tap(FakeDesktop.VK_BACK);
        _desktop.Type("d ");
        Assert.Equal("안녕 ", _desktop.Box.Text);
    }

    [Fact]
    public void ExceptionWordIsLeftAlone()
    {
        _settings.ExceptionWords.Add("qkqh");
        Typed(Layout.English, "qkqh ");
        Assert.Equal("qkqh ", _desktop.Box.Text);
    }

    [Fact]
    public void AutoModeOffLeavesEverything()
    {
        _settings.AutoMode = false;
        Typed(Layout.English, "dkssud ");
        Assert.Equal("dkssud ", _desktop.Box.Text);
    }

    [Fact]
    public void HotkeyAfterAutoConversionReverts()
    {
        Typed(Layout.English, "dkssud ").Hotkey();
        Assert.Equal("dkssud ", _desktop.Box.Text);
    }

    [Fact]
    public void ClickStartsOver() // a click may move the caret anywhere; nothing before it is touched
    {
        Typed(Layout.English, "dkssud");
        _desktop.Click();
        _desktop.Hotkey();
        Assert.Equal("dkssud", _desktop.Box.Text);
    }

    [Fact]
    public void SelectionIsConvertedWhenNothingWasTyped()
    {
        _desktop.Selection = "dkssud";
        _desktop.Hotkey();
        Assert.Equal("안녕", _desktop.Box.Text);   // typing over a selection replaces it; the box here was empty
    }
}

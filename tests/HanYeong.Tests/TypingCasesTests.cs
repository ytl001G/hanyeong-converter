using HanYeong.Core;

namespace HanYeong.Tests;

/// <summary>Every case in tests/typing.tsv, typed into the whole app without a screen.</summary>
public class TypingCasesTests
{
    public static IEnumerable<object[]> Cases() =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "typing.tsv"))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split('\t'))
            .Select(f => new object[] { f[0], f[1], f[2] });

    [Theory]
    [MemberData(nameof(Cases))]
    public void Typed(string mode, string typed, string expected)
    {
        var layout = mode == "KO" ? Layout.Korean : Layout.English;
        // Hangul stands for the keys that type it; the line ends with Space, or Enter (⏎), or its own period.
        string keys = string.Concat(typed.Select(c => Jamo.IsHangul(c) ? LayoutConverter.ToEnglish(c.ToString()) : c.ToString()));
        string end = keys.EndsWith('⏎') ? "\n" : keys.EndsWith('.') ? "" : " ";
        keys = keys.TrimEnd('⏎') + end;
        if (expected == "=")
        {
            using var plain = new ControllerTests(autoMode: false);
            expected = plain.Type(layout, keys).TrimEnd('\n', ' ');
        }
        using var app = new ControllerTests();
        Assert.Equal(expected + end, app.Type(layout, keys));
    }
}

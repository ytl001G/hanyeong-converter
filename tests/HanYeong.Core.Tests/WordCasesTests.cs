namespace HanYeong.Core.Tests;

/// <summary>Every case in tests/words.tsv: one word judged on its own.</summary>
public class WordCasesTests
{
    static readonly AutoDetector Detector = new();

    public static IEnumerable<object[]> Cases() =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "words.tsv"))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split('\t'))
            .Select(f => new object[] { f[0], f[1], f[2] });

    [Theory]
    [MemberData(nameof(Cases))]
    public void Word(string mode, string typed, string expected)
    {
        var layout = mode == "KO" ? Layout.Korean : Layout.English;
        // Hangul stands for the keys that type it; the screen shows what those keys give in this mode.
        string keys = string.Concat(typed.Select(c => Jamo.IsHangul(c) ? LayoutConverter.ToEnglish(c.ToString()) : c.ToString()));
        string screen = layout == Layout.English ? keys : LayoutConverter.ToKorean(keys);
        string? result = Detector.Suggest(screen, keys, layout);
        // In Korean mode the app puts back the capitals as typed.
        if (result is not null && layout == Layout.Korean && string.Equals(result, keys, StringComparison.OrdinalIgnoreCase)) result = keys;
        Assert.Equal(expected == "=" ? null : expected, result);
    }
}

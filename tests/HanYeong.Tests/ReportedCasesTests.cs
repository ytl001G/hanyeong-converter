using HanYeong.Core;

namespace HanYeong.Tests;

/// <summary>Every case in reported-cases.tsv, typed into the whole app without a screen.</summary>
public class ReportedCasesTests
{
    public static IEnumerable<object[]> Cases() =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "reported-cases.tsv"))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split('\t'))
            .Select(f => new object[] { f[0], f[1], f[2], f[3] });

    [Theory]
    [MemberData(nameof(Cases))]
    public void Typed(string mode, string keys, string ending, string expected)
    {
        string end = ending switch { "space" => " ", "enter" => "\n", "." => ".", _ => throw new ArgumentException(ending) };
        using var app = new ControllerTests();
        Assert.Equal(expected + end, app.Type(mode == "KO" ? Layout.Korean : Layout.English, keys + end));
    }
}

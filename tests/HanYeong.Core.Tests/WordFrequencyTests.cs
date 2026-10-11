using System.Globalization;
using System.IO.Compression;
using HanYeong.Core;

namespace HanYeong.Core.Tests;

/// <summary>The packed tables must answer exactly like a plain dictionary built from the same data.</summary>
public class WordFrequencyTests
{
    static Dictionary<string, float> Reference(string resource, StringComparer comparer)
    {
        var map = new Dictionary<string, float>(comparer);
        using var stream = typeof(WordFrequency).Assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress));
        while (reader.ReadLine() is { } line)
        {
            int tab = line.IndexOf('\t');
            map[line[..tab]] = float.Parse(line.AsSpan(tab + 1), CultureInfo.InvariantCulture);
        }
        return map;
    }

    [Fact]
    public void EnglishMatchesForEveryWordAndCase()
    {
        var reference = Reference("en.tsv.gz", StringComparer.OrdinalIgnoreCase);
        var packed = WordFrequency.English.Value;
        Assert.Equal(reference.Count, packed.Count);
        foreach (var (word, score) in reference)
        {
            Assert.Equal(score, packed.Score(word));
            Assert.Equal(score, packed.Score(word.ToUpperInvariant()));
            Assert.Equal(score, packed.Score(char.ToUpperInvariant(word[0]) + word[1..]));
        }
    }

    [Fact]
    public void KoreanMatchesForEveryWord()
    {
        var reference = Reference("ko.tsv.gz", StringComparer.Ordinal);
        var packed = WordFrequency.Korean.Value;
        Assert.Equal(reference.Count, packed.Count);
        foreach (var (word, score) in reference) Assert.Equal(score, packed.Score(word));
    }

    [Theory]
    [InlineData("dkssud")]
    [InlineData("zzzzzzzzzz")]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void MissingEnglishScoresZero(string word) => Assert.Equal(0f, WordFrequency.English.Value.Score(word));

    [Theory]
    [InlineData("뷁뷁뷁")]
    [InlineData("hello")]
    [InlineData("")]
    public void MissingKoreanScoresZero(string word) => Assert.Equal(0f, WordFrequency.Korean.Value.Score(word));
}

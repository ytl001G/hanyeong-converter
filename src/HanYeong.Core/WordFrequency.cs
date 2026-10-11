using System.Globalization;
using System.IO.Compression;

namespace HanYeong.Core;

/// <summary>
/// Word → Zipf-like score (log10 occurrences per billion words; ~1 rare, ~3 ordinary, 6+ very common),
/// built by tools/build_dictionaries.py from several corpora. Missing words score 0.
/// </summary>
public sealed class WordFrequency
{
    public static readonly Lazy<WordFrequency> English = new(() => Load("en.tsv.gz", StringComparer.OrdinalIgnoreCase));
    public static readonly Lazy<WordFrequency> Korean = new(() => Load("ko.tsv.gz", StringComparer.Ordinal));

    readonly Dictionary<string, float> _scores;

    WordFrequency(Dictionary<string, float> scores) => _scores = scores;

    public int Count => _scores.Count;

    public float Score(string word) => _scores.TryGetValue(word, out float s) ? s : 0f;

    static WordFrequency Load(string resource, StringComparer comparer)
    {
        var scores = new Dictionary<string, float>(comparer);
        using var stream = typeof(WordFrequency).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing resource {resource}");
        using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress));
        while (reader.ReadLine() is { } line)
        {
            int tab = line.IndexOf('\t');
            if (tab > 0) scores[line[..tab]] = float.Parse(line.AsSpan(tab + 1), CultureInfo.InvariantCulture);
        }
        return new WordFrequency(scores);
    }
}

using System.IO.Compression;

namespace HanYeong.Core;

/// <summary>
/// Word → Zipf-like score (log10 occurrences per billion words; ~1 rare, ~3 ordinary, 6+ very common),
/// built by tools/build_dictionaries.py from several corpora. Missing words score 0.
/// </summary>
/// <remarks>
/// About a million words, so they are packed instead of kept as strings in a dictionary (~70 MB → ~15 MB):
/// all words back to back in one sorted char array, an offset per word, and the score in tenths as a byte
/// (the data has one decimal, so nothing is lost). Lookup is a binary search.
/// </remarks>
public sealed class WordFrequency
{
    public static readonly Lazy<WordFrequency> English = new(() => Load("en.tsv.gz", isCaseInsensitive: true));
    public static readonly Lazy<WordFrequency> Korean = new(() => Load("ko.tsv.gz", isCaseInsensitive: false));

    readonly char[] _chars;
    readonly int[] _starts;     // word i is _chars[_starts[i] .. _starts[i + 1]]
    readonly byte[] _tenths;
    readonly bool _isCaseInsensitive;

    WordFrequency(char[] chars, int[] starts, byte[] tenths, bool isCaseInsensitive)
    {
        _chars = chars;
        _starts = starts;
        _tenths = tenths;
        _isCaseInsensitive = isCaseInsensitive;
    }

    public int Count => _tenths.Length;

    public float Score(string word)
    {
        if (!_isCaseInsensitive) return Find(word);
        // The English table is stored in lowercase ASCII; lowercasing the query matches it case-insensitively.
        Span<char> lower = word.Length <= 64 ? stackalloc char[word.Length] : new char[word.Length];
        for (int i = 0; i < word.Length; i++) lower[i] = char.IsAsciiLetterUpper(word[i]) ? (char)(word[i] | 0x20) : word[i];
        return Find(lower);
    }

    float Find(ReadOnlySpan<char> word)
    {
        int lo = 0, hi = _tenths.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int order = _chars.AsSpan(_starts[mid], _starts[mid + 1] - _starts[mid]).SequenceCompareTo(word);
            if (order == 0) return _tenths[mid] / 10f;
            if (order < 0) lo = mid + 1;
            else hi = mid - 1;
        }
        return 0f;
    }

    static WordFrequency Load(string resource, bool isCaseInsensitive)
    {
        using var stream = typeof(WordFrequency).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing resource {resource}");
        using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress));

        var chars = new List<char>(4_000_000);
        var starts = new List<int>(500_000);
        var tenths = new List<byte>(500_000);
        string? previous = null;
        while (reader.ReadLine() is { } line)
        {
            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            string word = line[..tab];
            // tools/build_dictionaries.py writes words in ordinal order; binary search depends on it.
            if (previous is not null && string.CompareOrdinal(previous, word) >= 0)
                throw new InvalidDataException($"{resource} is not sorted at '{word}'");
            previous = word;
            // Scores have one decimal and stay well under 25.5, so tenths fit a byte exactly.
            int value = (int)Math.Round(double.Parse(line.AsSpan(tab + 1), System.Globalization.CultureInfo.InvariantCulture) * 10);
            if (value is < 0 or > 255) throw new InvalidDataException($"{resource}: score out of range at '{word}'");
            starts.Add(chars.Count);
            foreach (char c in word) chars.Add(c);
            tenths.Add((byte)value);
        }
        starts.Add(chars.Count);
        return new WordFrequency(chars.ToArray(), starts.ToArray(), tenths.ToArray(), isCaseInsensitive);
    }
}

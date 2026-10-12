using System.IO.Compression;

namespace HanYeong.Core;

/// <summary>
/// Personal and place names the word lists miss (Ndidi, Kowalczyk), from tools/build_names.py.
/// Kept as a Bloom filter: 660k names in about 1.3MB instead of 10MB+ as strings. About 1 in 2,000 other words
/// also tests positive, which only matters for capitalized words (they are then left as English).
/// </summary>
public sealed class NameList
{
    public static readonly Lazy<NameList> Default = new(Load);

    const int HashCount = 11;
    readonly ulong[] _bits;
    readonly ulong _bitCount;

    NameList(int expected)
    {
        // 16 bits per name and 11 hashes give about 0.05% false positives (10 bits gave 1%, and 꾸준한 typed as
        // Rnwnsgks was taken for a name).
        _bitCount = (ulong)Math.Max(64, expected * 16);
        _bits = new ulong[(_bitCount + 63) / 64];
    }

    public bool Contains(string word)
    {
        var (h1, h2) = Hash(word);
        for (ulong i = 0; i < HashCount; i++)
        {
            ulong bit = (h1 + i * h2) % _bitCount;
            if ((_bits[bit / 64] & (1UL << (int)(bit % 64))) == 0) return false;
        }
        return true;
    }

    void Add(string word)
    {
        var (h1, h2) = Hash(word);
        for (ulong i = 0; i < HashCount; i++)
        {
            ulong bit = (h1 + i * h2) % _bitCount;
            _bits[bit / 64] |= 1UL << (int)(bit % 64);
        }
    }

    // FNV-1a over the lowercase letters, split into two hashes for double hashing.
    static (ulong, ulong) Hash(string word)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in word)
        {
            h ^= char.ToLowerInvariant(c);
            h *= 1099511628211UL;
        }
        ulong h2 = (h >> 33) ^ (h * 0x9E3779B97F4A7C15UL);
        return (h, h2 | 1);
    }

    static NameList Load()
    {
        using var stream = typeof(NameList).Assembly.GetManifestResourceStream("names.txt.gz")
            ?? throw new InvalidOperationException("Missing resource names.txt.gz");
        using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress));
        var names = new List<string>(700_000);
        while (reader.ReadLine() is { Length: > 0 } line) names.Add(line);
        var list = new NameList(names.Count);
        foreach (string name in names) list.Add(name);
        return list;
    }
}

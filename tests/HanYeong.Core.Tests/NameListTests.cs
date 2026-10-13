using System.IO.Compression;
using HanYeong.Core;

namespace HanYeong.Core.Tests;

public class NameListTests
{
    // Every name that reads as another listed name + particles must be on names-like-particles.txt, or it would be
    // split when typed right (Dobberkau → Dobbe가며).
    [Fact]
    public void NamesLikeParticlesIsComplete()
    {
        var names = NameList.Default.Value;
        bool IsName(string w) => w.Length >= 4 && names.Contains(w);
        using var stream = typeof(NameList).Assembly.GetManifestResourceStream("names.txt.gz")!;
        using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress));
        var expected = new SortedSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            string name = line.Trim();
            if (name.Length >= 7 && name.All(char.IsAsciiLetter) && AutoDetector.SplitsAsNameAndParticles(name.ToLowerInvariant(), IsName))
                expected.Add(name.ToLowerInvariant());
        }
        string file = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "HanYeong.Core", "Data", "names-like-particles.txt");
        var listed = new SortedSet<string>(File.ReadLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')), StringComparer.Ordinal);
        Assert.Equal(string.Join("\n", expected), string.Join("\n", listed));
    }
}

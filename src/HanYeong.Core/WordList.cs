namespace HanYeong.Core;

/// <summary>
/// The small hand-kept lists in Data/*.txt (particles, counters, prefixes before numbers ...): one entry per
/// line, # starts a comment. Kept as files so a word can be added without touching the code.
/// </summary>
static class WordList
{
    public static HashSet<string> Load(string resource)
    {
        using var stream = typeof(WordList).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing resource {resource}");
        using var reader = new StreamReader(stream);
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            string entry = line.Trim();
            if (entry.Length > 0 && !entry.StartsWith('#')) set.Add(entry);
        }
        return set;
    }
}

// Generates Korean and English sentences (with English + particles, numbers + counters, acronyms, names, quotes)
// and types them into the whole app without a screen, in the wrong mode and in the right one, until enough of
// them come out wrong.
// Usage: dotnet run -c Release --project tools/Fuzz -- [--seconds 60] [--failures 20] [--seed N]
using System.IO.Compression;
using System.Text;
using HanYeong;
using HanYeong.Core;
using HanYeong.Tests;

Console.OutputEncoding = Encoding.UTF8;
int seconds = Arg("--seconds", 60), maxFailures = Arg("--failures", 20), seed = Arg("--seed", Environment.TickCount);
int Arg(string name, int fallback) => Array.IndexOf(args, name) is var i and >= 0 ? int.Parse(args[i + 1]) : fallback;

Environment.SetEnvironmentVariable("HANYEONG_SETTINGS", Path.Combine(Path.GetTempPath(), $"hanyeong-fuzz-{Environment.ProcessId}.json"));
Environment.SetEnvironmentVariable("HANYEONG_LOG", Path.Combine(Path.GetTempPath(), $"hanyeong-fuzz-{Environment.ProcessId}.log"));

string data = Path.Combine(FindRepo(), "src", "HanYeong.Core", "Data");
static string FindRepo()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "HanYeong.slnx"))) return dir.FullName;
    throw new DirectoryNotFoundException("repository root (HanYeong.slnx) not found");
}
static IEnumerable<(string Word, float Score)> Table(string path)
{
    using var reader = new StreamReader(new GZipStream(File.OpenRead(path), CompressionMode.Decompress));
    while (reader.ReadLine() is { } line)
        if (line.Split('\t') is [var w, var s]) yield return (w, float.Parse(s, System.Globalization.CultureInfo.InvariantCulture));
}
static string[] List(string path) => File.ReadLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();

var koreanWords = Table(Path.Combine(data, "ko.tsv.gz")).Where(e => e.Score >= 4.0f && e.Word.All(Jamo.IsSyllable)).Select(e => e.Word).ToArray();
var englishWords = Table(Path.Combine(data, "en.tsv.gz")).Where(e => e.Score >= 4.0f && e.Word.Length >= 2 && e.Word.All(char.IsAsciiLetterLower)).Select(e => e.Word).ToArray();
var rareEnglish = Table(Path.Combine(data, "en.tsv.gz")).Where(e => e.Score is >= 1.5f and < 3.0f && e.Word.Length >= 4 && e.Word.All(char.IsAsciiLetterLower)).Select(e => e.Word).ToArray();
using var nameReader = new StreamReader(new GZipStream(File.OpenRead(Path.Combine(data, "names.txt.gz")), CompressionMode.Decompress));
var names = new List<string>();
while (nameReader.ReadLine() is { } n) if (names.Count < 200_000) names.Add(char.ToUpperInvariant(n[0]) + n[1..]);
var particles = List(Path.Combine(data, "particles.txt"));
var counters = List(Path.Combine(data, "counters.txt"));
string[] acronyms = ["SNS", "AI", "TV", "PC", "CEO", "FTA", "IT", "NASA", "DNA", "USB", "GPS", "KBS", "LG", "SK", "KT", "BTS", "UN", "EU", "IMF", "MRI"];
string[] contractions = ["don't", "it's", "I'm", "can't", "won't", "that's", "isn't", "you're"];
Console.WriteLine($"seed {seed}: {koreanWords.Length} Korean, {englishWords.Length} English words, {names.Count} names");

var rng = new Random(seed);
T Pick<T>(IReadOnlyList<T> items) => items[rng.Next(items.Count)];
string Particle() => Pick(particles);

string KoreanToken() => rng.Next(100) switch
{
    < 60 => Pick(koreanWords),
    < 70 => Pick(englishWords) + Particle(),                      // shortage이다
    < 75 => Pick(rareEnglish) + Particle(),                       // gyrus에
    < 82 => rng.Next(1, 300) + Pick(counters),                    // 3개
    < 88 => Pick(acronyms) + Particle(),                          // SNS를
    < 92 => Pick(names) + Particle(),                             // Ndidi는
    < 96 => "“" + Pick(koreanWords) + "”",
    _ => Pick(koreanWords) + "(" + Pick(acronyms) + ")",
};
string EnglishToken() => rng.Next(100) switch
{
    < 75 => Pick(englishWords),
    < 83 => Pick(names),
    < 88 => Pick(acronyms),
    < 93 => Pick(contractions),
    < 97 => rng.Next(1, 100) + Pick(new[] { "th", "st", "kg", "km", "s" }),
    _ => Pick(rareEnglish),
};
string Sentence(Func<string> token)
{
    var words = Enumerable.Range(0, rng.Next(3, 9)).Select(_ => token()).ToList();
    if (char.IsAsciiLetterLower(words[0][0]) && token == EnglishToken) words[0] = char.ToUpperInvariant(words[0][0]) + words[0][1..];
    return string.Join(' ', words);
}

// The keys for text typed in the given mode: Hangul as its keys, everything else as it is.
static string KeysFor(string text) => string.Concat(text.Select(c => Jamo.IsHangul(c) ? LayoutConverter.ToEnglish(c.ToString()) : c.ToString()));
static bool IsTypable(string keys) => keys.All(c => char.IsAsciiLetterOrDigit(c) || " .,-+'()".Contains(c));

string Type(Layout mode, string keys)
{
    var context = new ManualContext();
    SynchronizationContext.SetSynchronizationContext(context);
    var desktop = new FakeDesktop { Mode = mode };
    using var controller = new Controller(new Settings(), desktop, hooks: false);
    desktop.Attach(controller);
    controller.Ready.Wait();
    desktop.Type(keys);
    context.RunUntil(() => controller.Sending.IsCompleted && context.IsIdle);
    return desktop.Box.Text;
}

// A word both languages have as a common word (내/so, 해/go) cannot be told apart without context.
var detector = new AutoDetector();
// Also a jamo said twice (ㅎㅎ is gg, a laugh), and a word of up to three letters with a particle (go므로 → 해므로).
bool IsAmbiguous(string word) => word.Length >= 2 && word.All(c => c == word[0]) ? true
    : word.TakeWhile(char.IsAsciiLetter).Count() is var latin && latin is > 0 and <= 3 && latin < word.Length && word[latin..].All(Jamo.IsSyllable) ? true
    : IsAmbiguousWord(word);
bool IsAmbiguousWord(string word) =>
    word.All(Jamo.IsSyllable) ? detector.IsKnownWord(LayoutConverter.ToEnglish(word), Layout.English) && WordFrequency.Korean.Value.Score(word) > 0
    : word.All(char.IsAsciiLetter) && detector.IsKnownWord(word, Layout.English) && WordFrequency.Korean.Value.Score(LayoutConverter.ToKorean(word)) > 0;

// Left as typed on purpose: a rarer English word + a two-key particle English words often end in (Gabin지,
// epithets고): it reads as a name just as well.
bool IsByDesign(string word)
{
    int latin = word.TakeWhile(char.IsAsciiLetter).Count();
    if (latin == 0 || latin == word.Length) return false;
    string korean = word[latin..].TrimEnd('.');
    return korean.All(Jamo.IsSyllable) && LayoutConverter.ToEnglish(korean).Length <= 2 && korean is not ("이" or "에" or "도" or "로")
        && WordFrequency.English.Value.Score(word[..latin]) < 3.5f;
}

var failures = new List<string>();
var ambiguous = new Dictionary<string, int>();
var byDesign = new Dictionary<string, int>();
var byKind = new Dictionary<string, int>();
int sentences = 0;
var clock = System.Diagnostics.Stopwatch.StartNew();
while (clock.Elapsed.TotalSeconds < seconds && failures.Count < maxFailures)
{
    bool korean = rng.Next(2) == 0;
    string sentence = Sentence(korean ? KoreanToken : EnglishToken);
    string ending = rng.Next(4) == 0 ? "." : " ";
    string keys = KeysFor(sentence);
    if (!IsTypable(keys)) continue;
    sentences++;
    // Wrong mode: Korean typed in English mode, English typed in Korean mode. Right mode: must stay as typed
    // (only sentences of one script, since mixed ones need the 한/영 key in the middle).
    var checks = new List<(string Kind, Layout Mode)> { (korean ? "korean typed in EN mode" : "english typed in KO mode", korean ? Layout.English : Layout.Korean) };
    bool singleScript = korean ? !sentence.Any(char.IsAsciiLetter) : !sentence.Any(Jamo.IsHangul);
    if (singleScript) checks.Add((korean ? "korean typed right" : "english typed right", korean ? Layout.Korean : Layout.English));
    foreach (var (kind, mode) in checks)
    {
        string expected = sentence + ending;
        string got = Type(mode, keys + ending);
        if (got == expected) continue;
        var want = expected.Split(' ');
        var have = got.Split(' ');
        var bad = want.Zip(have).Where(p => p.First != p.Second).ToList();
        if (want.Length == have.Length && bad.All(p => IsAmbiguous(p.First.TrimEnd('.')) || IsByDesign(p.First)))
        {
            foreach (var (w, _) in bad)
            {
                var bucket = IsByDesign(w) ? byDesign : ambiguous;
                bucket[w.TrimEnd('.')] = bucket.GetValueOrDefault(w.TrimEnd('.')) + 1;
            }
            continue;
        }
        byKind[kind] = byKind.GetValueOrDefault(kind) + 1;
        string words = want.Length == have.Length ? string.Join(", ", bad.Select(p => $"{p.First} → {p.Second}")) : "(word count changed)";
        failures.Add($"[{kind}] {words}\n    typed:    {keys + ending}\n    expected: {expected}\n    got:      {got}");
    }
}

Console.WriteLine($"{sentences} sentences in {clock.Elapsed.TotalSeconds:F0}s, {failures.Count} failures " +
    $"({string.Join(", ", byKind.Select(kv => $"{kv.Key}: {kv.Value}"))}), seed {seed}");
foreach (string f in failures) Console.WriteLine(f);
if (byDesign.Count > 0)
    Console.WriteLine($"rare English + two-key particle, left as typed by design: {byDesign.Count} ({string.Join(", ", byDesign.Keys.Take(8))})");
if (ambiguous.Count > 0)
    Console.WriteLine("ambiguous in both languages (left to the mode, as intended): " +
        string.Join(", ", ambiguous.OrderByDescending(kv => kv.Value).Take(20).Select(kv => $"{kv.Key}({kv.Value})")));
return failures.Count == 0 ? 0 : 1;

// Generates Korean and English sentences (with English + particles, numbers + counters, decimals, currency,
// acronyms, abbreviations, names, quotes) and types them into the whole app without a screen: in the wrong mode,
// in the right one, and mixed sentences the way they are typed on purpose, switching with the 한/영 key.
// Failures are grouped by the kind of word that came out wrong, most frequent first.
// Usage: dotnet run -c Release --project tools/Fuzz -- [--seconds 60] [--sentences N] [--failures 200] [--examples 3] [--seed N]
//   --sentences  stop after this many sentences: the same seed then gives the same run on any machine
using System.IO.Compression;
using System.Text;
using HanYeong;
using HanYeong.Core;
using HanYeong.Tests;

Console.OutputEncoding = Encoding.UTF8;
int seconds = Arg("--seconds", 60), sentenceLimit = Arg("--sentences", 0), maxFailures = Arg("--failures", 200);
int examplesPerKind = Arg("--examples", 3), seed = Arg("--seed", Environment.TickCount);
int Arg(string name, int fallback) => Array.IndexOf(args, name) is var i and >= 0 ? int.Parse(args[i + 1]) : fallback;
if (sentenceLimit > 0 && Array.IndexOf(args, "--seconds") < 0) seconds = int.MaxValue;

string settingsPath = Path.Combine(Path.GetTempPath(), $"hanyeong-fuzz-{Environment.ProcessId}.json");
string logPath = Path.Combine(Path.GetTempPath(), $"hanyeong-fuzz-{Environment.ProcessId}.log");
Environment.SetEnvironmentVariable("HANYEONG_SETTINGS", settingsPath);
Environment.SetEnvironmentVariable("HANYEONG_LOG", logPath);
// Every run would leave its settings and a log of each sentence (hundreds of MB over a day of runs).
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    try { File.Delete(settingsPath); File.Delete(logPath); } catch (IOException) { }
};

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

var koreanTable = Table(Path.Combine(data, "ko.tsv.gz")).Where(e => e.Word.All(Jamo.IsSyllable) && AutoDetector.IsWellFormed(e.Word)).ToArray();
var koreanWords = koreanTable.Where(e => e.Score >= 4.0f).Select(e => e.Word).ToArray();
// Rarer Korean that people still type (score 2.5-4): the app must not need a word to be common.
var rareKorean = koreanTable.Where(e => e.Score is >= 2.5f and < 4.0f && e.Word.Length >= 2).Select(e => e.Word).ToArray();
var englishTable = Table(Path.Combine(data, "en.tsv.gz")).Where(e => e.Word.All(char.IsAsciiLetterLower)).ToArray();
var englishWords = englishTable.Where(e => e.Score >= 4.0f && e.Word.Length >= 2).Select(e => e.Word).ToArray();
var rareEnglish = englishTable.Where(e => e.Score is >= 1.5f and < 3.0f && e.Word.Length >= 4).Select(e => e.Word).ToArray();
// Lowercase abbreviations without a vowel (std, pdf, dll), as written before a particle.
// Not those that read as Korean (wpsl is 제니 typed in English mode, which the English list picked up).
var abbreviations = englishTable.Where(e => e.Score >= 2.5f && e.Word.Length is 3 or 4 && !e.Word.Any("aeiouy".Contains)
    && !AutoDetector.IsWellFormed(LayoutConverter.ToKorean(e.Word))).Select(e => e.Word).ToArray();
using var nameReader = new StreamReader(new GZipStream(File.OpenRead(Path.Combine(data, "names.txt.gz")), CompressionMode.Decompress));
var names = new List<string>();
while (nameReader.ReadLine() is { } n) if (names.Count < 200_000) names.Add(char.ToUpperInvariant(n[0]) + n[1..]);
var particles = List(Path.Combine(data, "particles.txt"));
var counters = List(Path.Combine(data, "counters.txt"));
string[] acronyms = ["SNS", "AI", "TV", "PC", "CEO", "FTA", "IT", "NASA", "DNA", "USB", "GPS", "KBS", "LG", "SK", "KT", "BTS", "UN", "EU", "IMF", "MRI"];
string[] contractions = ["don't", "it's", "I'm", "can't", "won't", "that's", "isn't", "you're"];
// Korean words that stand only before another word (새 물건, 두 개); a sentence may start with one.
string[] determiners = ["새", "헌", "첫", "그", "이", "저", "두", "세", "네", "각", "몇"];
string[] currencies = ["$", "$", "₩"];
Console.WriteLine($"seed {seed}: {koreanWords.Length} Korean (+{rareKorean.Length} rarer), {englishWords.Length} English (+{rareEnglish.Length} rarer, " +
    $"{abbreviations.Length} abbreviations), {names.Count} names");

var rng = new Random(seed);
T Pick<T>(IReadOnlyList<T> items) => items[rng.Next(items.Count)];
string Particle() => Pick(particles);
string Number() => rng.Next(1, 300).ToString();

string Capitalized(string w) => char.ToUpperInvariant(w[0]) + w[1..];
// The English ordinal ending: 1st, 2nd, 3rd, 11th-13th, 21st, the rest th.
static string Ordinal(int n) => (n % 100) is >= 11 and <= 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
string[] chatJamo = ["ㅋㅋ", "ㅋㅋㅋ", "ㅎㅎ", "ㅠㅠ", "ㅜㅜ", "ㅇㅇ", "ㄱㄱ", "ㅇㅋ"];
string[] urls = ["www.naver.com", "naver.com", "https://youtu.be/abc", "github.com/user", "www.example.com"];
string[] dotted = ["U.S.", "Mr.", "Dr.", "e.g.", "i.e.", "a.m.", "p.m."];
string[] timeParticles = ["에", "부터", "까지", "쯤", "에는"];

// Each word of a sentence with the kind of word it is, so failures can be grouped by it. Weights add up to 100.
(int Weight, string Kind, Func<string> Make)[] koreanKinds =
[
    (48, "korean word", () => Pick(koreanWords)),
    (5, "rarer korean word", () => Pick(rareKorean)),
    (8, "english + particle", () => Pick(englishWords) + Particle()),
    (4, "rarer english + particle", () => Pick(rareEnglish) + Particle()),
    (2, "abbreviation + particle", () => Pick(abbreviations) + Particle()),
    (5, "number + counter", () => Number() + Pick(counters)),
    (2, "decimal + counter/particle", () => $"{rng.Next(0, 10)}.{rng.Next(1, 10)}" + (rng.Next(2) == 0 ? Pick(counters) : Particle())),
    (2, "currency + particle", () => Pick(currencies) + Number() + Particle()),
    (1, "percent + particle", () => rng.Next(1, 100) + "%" + Particle()),
    (5, "acronym + particle", () => Pick(acronyms) + Particle()),
    (4, "name + particle", () => Pick(names) + Particle()),
    (3, "quoted", () => "\"" + Pick(koreanWords) + "\"" + (rng.Next(2) == 0 ? Particle() : "")),
    (2, "word(acronym)", () => Pick(koreanWords) + "(" + Pick(acronyms) + ")"),
    (2, "chat jamo", () => Pick(chatJamo)),
    (1, "number with comma + counter", () => $"{rng.Next(1, 100)},{rng.Next(0, 1000):000}" + Pick(counters)),
    (1, "time + particle", () => $"{rng.Next(1, 13)}:{rng.Next(0, 60):00}" + Pick(timeParticles)),
    (1, "url", () => Pick(urls)),
    (1, "email", () => $"user{rng.Next(1, 100)}@gmail.com"),
    (1, "hashtag", () => "#" + Pick(koreanWords)),
    (1, "emoticon", () => Pick(new[] { "^^", "^_^", "ㅎㅎ^^" })),
    (1, "word + ...", () => Pick(koreanWords) + "..."),
];
(int Weight, string Kind, Func<string> Make)[] englishKinds =
[
    (68, "english word", () => Pick(englishWords)),
    (8, "name", () => Pick(names)),
    (4, "acronym", () => Pick(acronyms)),
    (4, "contraction", () => Pick(contractions)),
    (3, "number + english ending", () => rng.Next(1, 100) is var n && rng.Next(2) == 0 ? n + Ordinal(n) : n + Pick(new[] { "kg", "km", "s" })),
    (3, "rarer english word", () => Pick(rareEnglish)),
    (1, "url", () => Pick(urls)),
    (1, "email", () => $"user{rng.Next(1, 100)}@gmail.com"),
    (1, "mention/hashtag", () => Pick(new[] { "@", "#" }) + Pick(englishWords)),
    (2, "all caps", () => Pick(englishWords).ToUpperInvariant()),
    (1, "code name", () => rng.Next(2) == 0 ? Pick(englishWords) + Capitalized(Pick(englishWords)) : Pick(englishWords) + "_" + Pick(englishWords)),
    (1, "dotted abbreviation", () => Pick(dotted)),
    (1, "number with comma/time", () => rng.Next(3) switch { 0 => $"{rng.Next(1, 100)},{rng.Next(0, 1000):000}", 1 => $"{rng.Next(1, 13)}:{rng.Next(0, 60):00}", _ => $"${rng.Next(1, 100)},000" }),
    (2, "possessive", () => Pick(englishWords) + "'s"),
];
(string Text, string Kind) Token((int Weight, string Kind, Func<string> Make)[] kinds)
{
    int roll = rng.Next(kinds.Sum(k => k.Weight));
    foreach (var (weight, kind, make) in kinds)
    {
        if (roll < weight) return (make(), kind);
        roll -= weight;
    }
    throw new InvalidOperationException();
}
(string Text, string Kind) KoreanToken() => Token(koreanKinds);
(string Text, string Kind) EnglishToken() => Token(englishKinds);
List<(string Text, string Kind)> Sentence(bool korean)
{
    var words = Enumerable.Range(0, rng.Next(3, 9)).Select(_ => korean ? KoreanToken() : EnglishToken()).ToList();
    if (korean && rng.Next(10) == 0) words.Insert(0, (Pick(determiners), "determiner first"));
    if (!korean && char.IsAsciiLetterLower(words[0].Text[0])) words[0] = (char.ToUpperInvariant(words[0].Text[0]) + words[0].Text[1..], words[0].Kind);
    return words;
}

// The keys for text typed in the given mode: Hangul as its keys, everything else as it is.
static string KeysFor(string text) => string.Concat(text.Select(c => Jamo.IsHangul(c) ? LayoutConverter.ToEnglish(c.ToString()) : c.ToString()));
static bool IsTypable(string keys) => keys.All(c => char.IsAsciiLetterOrDigit(c) || " .,-+'()\"$%?!:;/@#^_\n".Contains(c));

// English mode: a wrong letter typed inside a word and taken back with Backspace (keybaord → keyb + a + ⌫ + oard).
// Null when the keys have no place for one.
string? WithTypo(string keys)
{
    var spots = Enumerable.Range(1, Math.Max(0, keys.Length - 1)).Where(i => char.IsAsciiLetter(keys[i - 1]) && char.IsAsciiLetter(keys[i])).ToList();
    if (spots.Count == 0) return null;
    int at = Pick(spots);
    return keys[..at] + (char)('a' + rng.Next(26)) + "\b" + keys[at..];
}

// Types into a fresh app; with mixed set, each run of Hangul goes in Korean mode and each run of Latin letters in
// English mode, switching with the 한/영 key as a person does. Other characters stay in the mode before them.
// With typo set (English mode only), one wrong letter is typed and erased inside a word. With holdShift set, Shift
// stays down through each run of capitals and the space after it, as people type acronyms (PTE then Space).
// With shiftEnter set, a final Enter is Shift+Enter (a new line in chat apps).
string Type(Layout mode, string text, bool mixed = false, bool typo = false, bool holdShift = false, bool shiftEnter = false)
{
    var context = new ManualContext();
    SynchronizationContext.SetSynchronizationContext(context);
    var desktop = new FakeDesktop { Mode = mode };
    using var controller = new Controller(new Settings(), desktop, hooks: false);
    desktop.Attach(controller);
    controller.Ready.Wait();
    shiftEnter &= text.EndsWith('\n');
    if (shiftEnter) text = text[..^1];
    string keys = typo && WithTypo(KeysFor(text)) is { } withTypo ? withTypo : KeysFor(text);
    if (!mixed && holdShift)
        for (int i = 0; i < keys.Length;)
        {
            int end = i;
            while (end < keys.Length && char.IsAsciiLetterUpper(keys[end])) end++;
            if (end - i >= 2 && end < keys.Length && keys[end] == ' ')
            {
                desktop.Down(FakeDesktop.VK_LSHIFT);
                for (; i < end; i++) desktop.Tap(keys[i]);
                desktop.Tap(FakeDesktop.VK_SPACE);
                desktop.Up(FakeDesktop.VK_LSHIFT);
                i++;
            }
            else desktop.Type(keys[i++].ToString());
        }
    else if (!mixed) desktop.Type(keys);
    else
        foreach (char c in text)
        {
            Layout? wanted = Jamo.IsHangul(c) ? Layout.Korean : char.IsAsciiLetter(c) ? Layout.English : null;
            if (wanted is { } w && w != desktop.Mode)
            {
                context.RunUntil(() => controller.Sending.IsCompleted && context.IsIdle);
                desktop.Tap(FakeDesktop.VK_HANGUL);
            }
            desktop.Type(KeysFor(c.ToString()));
        }
    if (shiftEnter)
    {
        desktop.Down(FakeDesktop.VK_LSHIFT);
        desktop.Tap(FakeDesktop.VK_RETURN);
        desktop.Up(FakeDesktop.VK_LSHIFT);
    }
    context.RunUntil(() => controller.Sending.IsCompleted && context.IsIdle);
    return desktop.Box.Text;
}

if (Array.IndexOf(args, "--corpus") is var corpusArg and >= 0) return LongText(args[corpusArg + 1]);

// Long text: real sentences of the evaluation corpora (tools/Evaluation, docs/accuracy-report.md), several joined
// into a paragraph, typed through the app in the wrong mode (each word should come back as written) and in the
// right one, switching with the 한/영 key around the other script (nothing should change).
//   --corpus <folder with the *-sentences.txt files>  [--paragraphs 200] [--per-paragraph 6]
int LongText(string root)
{
    int paragraphCount = Arg("--paragraphs", 200), perParagraph = Arg("--per-paragraph", 6);
    Console.WriteLine($"long text: {paragraphCount} paragraphs of {perParagraph} sentences per corpus, seed {seed}");
    foreach (string file in Directory.GetFiles(root, "*-sentences.txt", SearchOption.AllDirectories).Order())
    {
        string name = Path.GetFileName(file).Replace("-sentences.txt", "");
        bool korean = name.StartsWith("kor");
        var pool = File.ReadLines(file).Select(l => Normalize(l[(l.IndexOf('\t') + 1)..]))
            .Where(s => s.Length > 0 && IsTypable(KeysFor(s))).ToList();
        var local = new Random(seed);
        long words = 0, wrongWords = 0, rightChanged = 0;
        int wrongParagraphsAllRight = 0, rightParagraphsUntouched = 0;
        var misses = new Dictionary<string, int>();
        var falsePositives = new Dictionary<string, int>();
        // The words around the first time each error was seen.
        var contexts = new Dictionary<string, string>();
        for (int p = 0; p < paragraphCount; p++)
        {
            string paragraph = string.Join(' ', Enumerable.Range(0, perParagraph).Select(_ => pool[local.Next(pool.Count)])) + " ";
            var want = paragraph.Split(' ');
            words += want.Length - 1;
            int wrongBefore = (int)wrongWords;
            Count(Type(korean ? Layout.English : Layout.Korean, paragraph), misses, ref wrongWords);
            if (wrongWords == wrongBefore) wrongParagraphsAllRight++;
            int rightBefore = (int)rightChanged;
            var startMode = paragraph.FirstOrDefault(c => Jamo.IsHangul(c) || char.IsAsciiLetter(c)) is var first && Jamo.IsHangul(first) ? Layout.Korean : Layout.English;
            Count(Type(startMode, paragraph, mixed: true), falsePositives, ref rightChanged);
            if (rightChanged == rightBefore) rightParagraphsUntouched++;

            void Count(string got, Dictionary<string, int> errors, ref long errorCount)
            {
                var have = got.Split(' ');
                if (have.Length != want.Length) { errorCount += want.Length - 1; errors["(word count changed)"] = errors.GetValueOrDefault("(word count changed)") + 1; return; }
                for (int i = 0; i < want.Length - 1; i++)
                {
                    if (want[i] == have[i]) continue;
                    errorCount++;
                    string pair = $"{want[i]} → {have[i]}";
                    errors[pair] = errors.GetValueOrDefault(pair) + 1;
                    contexts.TryAdd(pair, string.Join(' ', want[Math.Max(0, i - 4)..i]) + " [" + have[i] + "] " + string.Join(' ', want[(i + 1)..Math.Min(want.Length, i + 4)]));
                }
            }
        }
        Console.WriteLine($"\n{name} ({pool.Count} typable sentences, {words} words in {paragraphCount} paragraphs)");
        Console.WriteLine($"  typed in the wrong mode: {100.0 * (words - wrongWords) / words:F2}% of words come back right, " +
            $"{100.0 * wrongParagraphsAllRight / paragraphCount:F1}% of paragraphs entirely");
        Console.WriteLine($"  typed right:             {10000.0 * rightChanged / words:F1} words changed per 10,000, " +
            $"{100.0 * rightParagraphsUntouched / paragraphCount:F1}% of paragraphs untouched");
        Console.WriteLine("  most missed:  " + string.Join(", ", misses.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Key} ({kv.Value})")));
        Console.WriteLine("  changed by mistake: " + string.Join(", ", falsePositives.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Key} ({kv.Value})")));
        foreach (var (pair, _) in misses.Concat(falsePositives).OrderByDescending(kv => kv.Value).Take(examplesPerKind * 2))
            if (contexts.TryGetValue(pair, out var around)) Console.WriteLine($"    {pair}: ...{around}...");
    }
    return 0;
}

// Curly quotes and dashes as a keyboard types them.
static string Normalize(string s) => string.Join(' ', s.Replace('“', '"').Replace('”', '"').Replace('‘', '\'').Replace('’', '\'')
    .Replace('–', '-').Replace('—', '-').Replace("…", "...").Split(' ', StringSplitOptions.RemoveEmptyEntries));

// A word both languages have as a common word (내/so, 해/go) cannot be told apart without context.
var detector = new AutoDetector();
// Also a jamo said twice (ㅎㅎ is gg, a laugh), and a word of up to three letters with a particle (go므로 → 해므로).
// Chat jamo with or without an emoticon (ㅎㅎ^^ is gg^^, ㅇㅋ is dz) is left to the mode too.
bool IsAmbiguous(string word) => word.Length >= 2 && word.All(c => char.ToLowerInvariant(c) == char.ToLowerInvariant(word[0])) ? true
    : word.Where(Jamo.IsHangul).ToArray() is { Length: >= 2 and <= 3 } jamo && jamo.All(Jamo.IsJamo) && !word.Any(char.IsAsciiLetter) ? true
    : word.TakeWhile(char.IsAsciiLetter).Count() is var latin && latin is > 0 and <= 3 && latin < word.Length && word[latin..].All(Jamo.IsSyllable) ? true
    : IsAmbiguousWord(word);
bool IsAmbiguousWord(string word) =>
    word.Length > 1 && word[0] is '#' or '@' ? IsAmbiguousWord(word[1..])
    : word.All(Jamo.IsSyllable) ? detector.IsKnownWord(LayoutConverter.ToEnglish(word), Layout.English) && WordFrequency.Korean.Value.Score(word) > 0
    : word.All(char.IsAsciiLetter) && detector.IsKnownWord(word, Layout.English) && WordFrequency.Korean.Value.Score(LayoutConverter.ToKorean(word)) > 0;

// Left as typed on purpose: a rarer English word + a two-key particle English words often end in (Gabin지,
// epithets고): it reads as a name just as well.
bool IsByDesign(string word)
{
    int latin = word.TakeWhile(char.IsAsciiLetter).Count();
    if (latin == 0 || latin == word.Length) return false;
    string korean = word[latin..].TrimEnd('.', '?');
    return korean.All(Jamo.IsSyllable) && LayoutConverter.ToEnglish(korean).Length <= 2 && korean is not ("이" or "에" or "도" or "로")
        && WordFrequency.English.Value.Score(word[..latin]) < 3.5f;
}

var failures = new Dictionary<string, List<string>>();
int failureCount = 0;
var ambiguous = new Dictionary<string, int>();
var byDesign = new Dictionary<string, int>();
var checkCounts = new Dictionary<string, int>();
int sentences = 0;
var clock = System.Diagnostics.Stopwatch.StartNew();
string[] endings = [" ", " ", " ", ".", "?", "\n"];
while (clock.Elapsed.TotalSeconds < seconds && failureCount < maxFailures && (sentenceLimit == 0 || sentences < sentenceLimit))
{
    bool korean = rng.Next(2) == 0;
    var tokens = Sentence(korean);
    string sentence = string.Join(' ', tokens.Select(t => t.Text));
    string ending = Pick(endings);
    if (!IsTypable(KeysFor(sentence))) continue;
    sentences++;
    // Wrong mode: Korean typed in English mode, English typed in Korean mode. Right mode: must stay as typed;
    // a sentence of both scripts is typed with the 한/영 key in between.
    var checks = new List<(string Kind, Layout Mode, bool Mixed)> { (korean ? "korean typed in EN mode" : "english typed in KO mode", korean ? Layout.English : Layout.Korean, false) };
    bool singleScript = korean ? !sentence.Any(char.IsAsciiLetter) : !sentence.Any(Jamo.IsHangul);
    var rightMode = korean ? Layout.Korean : Layout.English;
    checks.Add(singleScript ? (korean ? "korean typed right" : "english typed right", rightMode, false)
        : ("mixed typed right (한/영 key)", sentence.First(c => Jamo.IsHangul(c) || char.IsAsciiLetter(c)) is var first && Jamo.IsHangul(first) ? Layout.Korean : Layout.English, true));
    foreach (var (checkKind, mode, mixed) in checks)
    {
        // Now and then a typo taken back with Backspace on the way (English mode, where one key is one letter).
        bool typo = !mixed && mode == Layout.English && rng.Next(4) == 0;
        // Now and then Shift held through an acronym and the space after it.
        bool holdShift = !mixed && !typo && System.Text.RegularExpressions.Regex.IsMatch(KeysFor(sentence + ending), "[A-Z]{2,} ") && rng.Next(3) == 0;
        bool shiftEnter = ending == "\n" && rng.Next(2) == 0;
        string kind = checkKind + (typo ? ", typo fixed with Backspace" : "") + (holdShift ? ", Shift held through capitals" : "") + (shiftEnter ? ", Shift+Enter" : "");
        checkCounts[kind] = checkCounts.GetValueOrDefault(kind) + 1;
        string expected = sentence + ending;
        string got = Type(mode, expected, mixed, typo, holdShift, shiftEnter);
        if (got == expected) continue;
        var want = expected.TrimEnd('\n').Split(' ');
        var have = got.TrimEnd('\n').Split(' ');
        var bad = Enumerable.Range(0, Math.Min(want.Length, have.Length)).Where(i => want[i] != have[i]).ToList();
        if (want.Length == have.Length && bad.All(i => IsAmbiguous(want[i].TrimEnd('.', '?')) || IsByDesign(want[i])))
        {
            foreach (int i in bad)
            {
                string w = want[i].TrimEnd('.', '?');
                var bucket = IsByDesign(want[i]) ? byDesign : ambiguous;
                bucket[w] = bucket.GetValueOrDefault(w) + 1;
            }
            continue;
        }
        failureCount++;
        // Grouped by the check and the kind of the first word that came out wrong.
        string tokenKind = want.Length == have.Length ? tokens[Math.Min(bad.First(i => !IsAmbiguous(want[i].TrimEnd('.', '?')) && !IsByDesign(want[i])), tokens.Count - 1)].Kind : "word count changed";
        string key = $"{kind} / {tokenKind}";
        string words = want.Length == have.Length ? string.Join(", ", bad.Select(i => $"{want[i]} → {have[i]}")) : "(word count changed)";
        if (!failures.TryGetValue(key, out var list)) failures[key] = list = [];
        list.Add($"{words}\n      typed:    {KeysFor(expected).Replace("\n", "⏎")}\n      expected: {expected.Replace("\n", "⏎")}\n      got:      {got.Replace("\n", "⏎")}");
    }
}

Console.WriteLine($"{sentences} sentences in {clock.Elapsed.TotalSeconds:F0}s, {failureCount} failures, seed {seed}" +
    (sentenceLimit > 0 ? $", --sentences {sentenceLimit}" : ""));
Console.WriteLine("checks: " + string.Join(", ", checkCounts.Select(kv => $"{kv.Key} {kv.Value}")));
foreach (var (key, list) in failures.OrderByDescending(kv => kv.Value.Count))
{
    Console.WriteLine($"\n[{list.Count}] {key}");
    foreach (string f in list.Take(examplesPerKind)) Console.WriteLine("  " + f);
}
if (byDesign.Count > 0)
    Console.WriteLine($"\nrare English + two-key particle, left as typed by design: {byDesign.Count} ({string.Join(", ", byDesign.Keys.Take(8))})");
if (ambiguous.Count > 0)
    Console.WriteLine("ambiguous in both languages (left to the mode, as intended): " +
        string.Join(", ", ambiguous.OrderByDescending(kv => kv.Value).Take(20).Select(kv => $"{kv.Key}({kv.Value})")));
return failureCount == 0 ? 0 : 1;

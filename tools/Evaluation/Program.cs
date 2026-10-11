// Accuracy evaluation for docs/accuracy-report.md.
// Usage: dotnet run -c Release --project tools/Evaluation -- <held-out folder> [results.json]
// The folder holds Leipzig corpora not used to build the word tables (see docs/accuracy-report.md):
//   eng_news_2024_100K, eng_wikipedia_2016_100K, kor-kr_web_2015_100K, kor_newscrawl_2016_100K
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HanYeong.Core;

Console.OutputEncoding = Encoding.UTF8;
string root = args[0];
string outPath = args.Length > 1 ? args[1] : "eval-results.json";
var report = new Dictionary<string, object>();

// ---------- load cost ----------
long memBefore = GC.GetTotalMemory(true);
var loadTimer = Stopwatch.StartNew();
_ = WordFrequency.English.Value;
_ = WordFrequency.Korean.Value;
_ = CommonSyllables.Count;
loadTimer.Stop();
long memAfter = GC.GetTotalMemory(true);
report["load"] = new
{
    ms = loadTimer.ElapsedMilliseconds,
    managedMB = Math.Round((memAfter - memBefore) / 1048576.0, 1),
    englishWords = WordFrequency.English.Value.Count,
    koreanWords = WordFrequency.Korean.Value.Count,
};

// ---------- corpora ----------
char[] wordEnds = [' ', '.', ',', '?', '!', ';', ':'];
var corpora = new (string Key, string Folder, Layout Lang)[]
{
    ("en_news", "eng_news_2024_100K", Layout.English),
    ("en_wiki", "eng_wikipedia_2016_100K", Layout.English),
    ("ko_web", "kor-kr_web_2015_100K", Layout.Korean),
    ("ko_news", "kor_newscrawl_2016_100K", Layout.Korean),
};

var sentences = new Dictionary<string, List<string[]>>();
var raw = new Dictionary<string, List<string>>();
foreach (var (key, folder, _) in corpora)
{
    string file = Directory.GetFiles(Path.Combine(root, folder), "*-sentences.txt", SearchOption.AllDirectories)[0];
    var rawList = new List<string>();
    var tokenized = new List<string[]>();
    foreach (string line in File.ReadLines(file))
    {
        int tab = line.IndexOf('\t');
        string s = tab >= 0 ? line[(tab + 1)..] : line;
        rawList.Add(s);
        tokenized.Add(s.Split(wordEnds, StringSplitOptions.RemoveEmptyEntries));
    }
    raw[key] = rawList;
    sentences[key] = tokenized;
}

static bool IsLatin(string w) => w.Length > 0 && w.All(char.IsAsciiLetter);
static bool IsHangul(string w) => w.Length > 0 && w.All(Jamo.IsSyllable);
// What ToEnglish gives back for typed keys: Shift only survives on QWERTOP (ㅃㅉㄸㄲㅆㅒㅖ).
static string CaseInsensitiveForm(string keys) =>
    new(keys.Select(c => char.IsAsciiLetterUpper(c) && !"QWERTOP".Contains(c) ? char.ToLowerInvariant(c) : c).ToArray());

// ---------- per-token outcomes, cached per detector ----------
// Correct typing: Latin in English mode, Hangul in Korean mode -> any conversion is a false positive.
// Wrong mode:     Latin typed in Korean mode, Hangul typed in English mode -> success if fixed exactly.
Dictionary<string, Outcome> Evaluate(AutoDetector d, IEnumerable<string> words)
{
    var map = new Dictionary<string, Outcome>(StringComparer.Ordinal);
    foreach (string w in words)
    {
        if (map.ContainsKey(w)) continue;
        if (IsLatin(w))
        {
            string? c = d.Check(w, Layout.English) ?? d.CheckMixed(w, Layout.English);
            string? r = d.Check(LayoutConverter.ToKorean(w), Layout.Korean);
            map[w] = new Outcome(c is not null, c, r is not null && r == CaseInsensitiveForm(w), r);
        }
        else if (IsHangul(w))
        {
            string? c = d.Check(w, Layout.Korean) ?? d.CheckMixed(LayoutConverter.ToEnglish(w), Layout.Korean);
            string? r = d.Check(LayoutConverter.ToEnglish(w), Layout.English);
            map[w] = new Outcome(c is not null, c, r == w, r);
        }
    }
    return map;
}

var allWords = sentences.Values.SelectMany(l => l).SelectMany(t => t).ToList();
var defaults = new AutoDetector();
var outcome = Evaluate(defaults, allWords);

object CorpusStats(string key, Layout lang, Dictionary<string, Outcome> o)
{
    long tokens = 0, letterTokens = 0, fp = 0, wrongEligible = 0, wrongFixed = 0;
    long sentencesWithFp = 0, sentencesAllFixed = 0, sentencesWithTarget = 0;
    double fixedShareSum = 0;
    foreach (var sent in sentences[key])
    {
        bool hasFp = false;
        int target = 0, fixedCount = 0;
        foreach (string w in sent)
        {
            tokens++;
            if (!o.TryGetValue(w, out var r)) continue;   // digits, quotes, mixed: never touched by the app
            letterTokens++;
            if (r.CorrectChanged) { fp++; hasFp = true; }
            // "Forgot to switch": only words of the sentence's own language are mistyped.
            bool isTarget = lang == Layout.English ? IsLatin(w) : IsHangul(w);
            if (!isTarget) continue;
            wrongEligible++;
            target++;
            if (r.WrongFixed) { wrongFixed++; fixedCount++; }
        }
        if (hasFp) sentencesWithFp++;
        if (target > 0)
        {
            sentencesWithTarget++;
            fixedShareSum += (double)fixedCount / target;
            if (fixedCount == target) sentencesAllFixed++;
        }
    }
    return new
    {
        sentences = sentences[key].Count,
        tokens,
        letterTokens,
        falsePositiveTokens = fp,
        falsePositivePerToken = (double)fp / letterTokens,
        sentencesWithFalsePositive = (double)sentencesWithFp / sentences[key].Count,
        wrongModeTokens = wrongEligible,
        wrongModeFixed = (double)wrongFixed / wrongEligible,
        wrongModeSentencesFullyFixed = (double)sentencesAllFixed / sentencesWithTarget,
        wrongModeAvgShareFixedPerSentence = fixedShareSum / sentencesWithTarget,
    };
}

var perCorpus = new Dictionary<string, object>();
foreach (var (key, _, lang) in corpora) perCorpus[key] = CorpusStats(key, lang, outcome);
report["corpora"] = perCorpus;

// ---------- error examples (token frequency weighted) ----------
var freq = allWords.Where(w => outcome.ContainsKey(w)).GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
object Top(Func<string, Outcome, bool> pick, Func<string, Outcome, string?> show, int n = 25) =>
    freq.Where(kv => pick(kv.Key, outcome[kv.Key])).OrderByDescending(kv => kv.Value).Take(n)
        .Select(kv => new { word = kv.Key, result = show(kv.Key, outcome[kv.Key]), count = kv.Value }).ToList();
report["examples"] = new
{
    falsePositiveLatin = Top((w, o) => IsLatin(w) && o.CorrectChanged, (w, o) => o.CorrectResult),
    falsePositiveHangul = Top((w, o) => IsHangul(w) && o.CorrectChanged, (w, o) => o.CorrectResult),
    missedHangulTypedInEnglish = Top((w, o) => IsHangul(w) && !o.WrongFixed, (w, o) => LayoutConverter.ToEnglish(w)),
    missedLatinTypedInKorean = Top((w, o) => IsLatin(w) && !o.WrongFixed && w.Length >= 2, (w, o) => LayoutConverter.ToKorean(w)),
};

// ---------- mixed words: English + Korean particle (AI가, SNS를) found in the Korean test text ----------
// Only "word + particle" (SNS를); compounds like LG전자 are a different thing and are not attempted.
var mixedPattern = new System.Text.RegularExpressions.Regex("^[A-Za-z]{2,}[가-힣]+$");
var mixedAll = sentences["ko_web"].Concat(sentences["ko_news"]).SelectMany(t => t)
    .Where(w => mixedPattern.IsMatch(w)).ToList();
var mixedFreq = mixedAll
    .Where(w => AutoDetector.IsParticleChain(w[w.TakeWhile(char.IsAsciiLetter).Count()..]))
    .GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
long mixedTotal = mixedFreq.Values.Sum(v => (long)v), mixedEnFixed = 0, mixedKoFixed = 0;
var mixedMisses = new Dictionary<string, int>();
foreach (var (w, count) in mixedFreq)
{
    int cut = w.TakeWhile(char.IsAsciiLetter).Count();
    string keys = w[..cut] + LayoutConverter.ToEnglish(w[cut..]);   // the whole word typed in one mode
    bool en = defaults.CheckMixed(keys, Layout.English) == w;
    bool ko = defaults.CheckMixed(keys, Layout.Korean) == w;
    if (en) mixedEnFixed += count;
    if (ko) mixedKoFixed += count;
    if (!en) mixedMisses[w] = count;
}
report["mixed"] = new
{
    latinHangulTokens = mixedAll.Count,
    distinctWords = mixedFreq.Count,
    tokens = mixedTotal,
    fixedWhenTypedInEnglishMode = (double)mixedEnFixed / mixedTotal,
    fixedWhenTypedInKoreanMode = (double)mixedKoFixed / mixedTotal,
    topMisses = mixedMisses.OrderByDescending(kv => kv.Value).Take(25).Select(kv => new { word = kv.Key, count = kv.Value }).ToList(),
};

// ---------- threshold sweep ----------
long Weighted(Dictionary<string, Outcome> o, Func<string, Outcome, bool> f) => freq.Where(kv => f(kv.Key, o[kv.Key])).Sum(kv => (long)kv.Value);
long latinTotal = freq.Where(kv => IsLatin(kv.Key)).Sum(kv => (long)kv.Value);
long hangulTotal = freq.Where(kv => IsHangul(kv.Key)).Sum(kv => (long)kv.Value);
var uniqueWords = freq.Keys.ToList();
object SweepPoint(string name, float value, AutoDetectorOptions opt)
{
    var o = Evaluate(new AutoDetector(options: opt), uniqueWords);
    return new
    {
        parameter = name,
        value,
        isDefault = opt == AutoDetectorOptions.Default,
        // English mode: real English wrongly turned Korean / Korean typed in English mode fixed
        enModeFalsePositive = (double)Weighted(o, (w, r) => IsLatin(w) && r.CorrectChanged) / latinTotal,
        enModeFixed = (double)Weighted(o, (w, r) => IsHangul(w) && r.WrongFixed) / hangulTotal,
        // Korean mode: real Korean wrongly turned English / English typed in Korean mode fixed
        koModeFalsePositive = (double)Weighted(o, (w, r) => IsHangul(w) && r.CorrectChanged) / hangulTotal,
        koModeFixed = (double)Weighted(o, (w, r) => IsLatin(w) && r.WrongFixed) / latinTotal,
    };
}
var d0 = AutoDetectorOptions.Default;
var sweep = new List<object>();
foreach (float v in new[] { 0f, 0.5f, 1.0f, 1.5f, 2.0f, 3.0f }) sweep.Add(SweepPoint("EnglishToKoreanMargin", v, d0 with { EnglishToKoreanMargin = v }));
foreach (float v in new[] { 0.5f, 1.0f, 1.5f, 2.0f, 2.5f }) sweep.Add(SweepPoint("UnseenKorean", v, d0 with { UnseenKorean = v }));
foreach (float v in new[] { 1.0f, 1.5f, 2.0f, 2.5f, 3.0f }) sweep.Add(SweepPoint("KoreanToEnglishMargin", v, d0 with { KoreanToEnglishMargin = v }));
foreach (float v in new[] { 2.5f, 3.0f, 3.5f, 4.0f, 4.5f }) sweep.Add(SweepPoint("CommonKorean", v, d0 with { CommonKorean = v }));
foreach (float v in new[] { 1.0f, 1.5f, 2.0f, 2.5f, 3.0f }) sweep.Add(SweepPoint("KnownEnglish", v, d0 with { KnownEnglish = v }));
report["sweep"] = sweep;

// ---------- nonsense input ----------
var rng = new Random(20261011);
string RandomString(string alphabet, int min, int max)
{
    int n = rng.Next(min, max + 1);
    var sb = new StringBuilder(n);
    for (int i = 0; i < n; i++) sb.Append(alphabet[rng.Next(alphabet.Length)]);
    return sb.ToString();
}
const string lower = "abcdefghijklmnopqrstuvwxyz";
var common = Enumerable.Range('가', '힣' - '가' + 1).Select(c => (char)c).Where(CommonSyllables.Contains).ToArray();
double Rate(int n, Func<string> make, Func<string, bool> converts) => Enumerable.Range(0, n).Count(_ => converts(make())) / (double)n;
const int N = 100_000;
report["nonsense"] = new
{
    randomLowercaseInEnglishMode = Rate(N, () => RandomString(lower, 3, 10), s => defaults.Check(s, Layout.English) is not null),
    randomLowercaseFormsValidHangul = Rate(N, () => RandomString(lower, 3, 10), s => AutoDetector.IsWellFormed(LayoutConverter.ToKorean(s))),
    randomKeysInKoreanMode = Rate(N, () => LayoutConverter.ToKorean(RandomString(lower, 3, 10)), s => defaults.Check(s, Layout.Korean) is not null),
    idLikeLettersDigits = Rate(N, () => RandomString(lower, 3, 8) + RandomString("0123456789", 1, 4), s => defaults.Check(s, Layout.English) is not null),
    passwordLike = Rate(N, () => RandomString(lower + lower.ToUpperInvariant() + "0123456789!@#$%", 8, 14), s => defaults.Check(s, Layout.English) is not null),
    // Strings that are valid Hangul syllables but (almost surely) not words, typed in English mode.
    randomSyllablesTypedInEnglish = Rate(N, () => LayoutConverter.ToEnglish(new string(Enumerable.Range(0, rng.Next(2, 5)).Select(_ => common[rng.Next(common.Length)]).ToArray())),
        s => defaults.Check(s, Layout.English) is not null),
};

// ---------- Ctrl+Space fidelity on whole sentences ----------
static bool TypableAscii(string s) => s.All(c => c is >= ' ' and <= '~');
object Fidelity(string key, Layout lang)
{
    int total = 0, exact = 0, reproducible = 0, reproducibleExact = 0;
    var failures = new List<object>();
    foreach (string s in raw[key])
    {
        string converted;
        if (lang == Layout.Korean)
        {
            // Korean sentence typed in English mode, converted back.
            var seg = new TypedSegment(Layout.English);
            foreach (char c in LayoutConverter.ToEnglish(s)) seg.Type(c);
            converted = seg.Converted;
        }
        else
        {
            if (!TypableAscii(s)) continue;     // only what a keyboard types directly
            var seg = new TypedSegment(Layout.Korean);
            foreach (char c in s) seg.Type(c);
            converted = seg.Converted;
        }
        total++;
        // Text that no key sequence reproduces: a lone jamo right after a syllable (요ㅋ re-typed is 욬),
        // or Latin letters inside Korean (0.01%p), which flip along with everything else.
        bool isReproducible = lang == Layout.English || !s.Any(c => Jamo.IsJamo(c) || char.IsAsciiLetter(c));
        if (isReproducible) reproducible++;
        if (converted == s) { exact++; if (isReproducible) reproducibleExact++; }
        else if (isReproducible && failures.Count < 8) failures.Add(new { original = s.Length > 80 ? s[..80] : s, converted = converted.Length > 80 ? converted[..80] : converted });
    }
    return new { sentences = total, exactRate = (double)exact / total, reproducibleSentences = reproducible, reproducibleExactRate = (double)reproducibleExact / reproducible, failures };
}
report["hotkey"] = new
{
    en_news = Fidelity("en_news", Layout.English),
    en_wiki = Fidelity("en_wiki", Layout.English),
    ko_web = Fidelity("ko_web", Layout.Korean),
    ko_news = Fidelity("ko_news", Layout.Korean),
};

// ---------- speed ----------
var sample = uniqueWords.Take(200_000).ToList();
var fresh = new AutoDetector();
var timer = Stopwatch.StartNew();
int checks = 0;
foreach (string w in sample)
{
    fresh.Check(w, IsLatin(w) ? Layout.English : Layout.Korean);
    checks++;
}
timer.Stop();
report["speed"] = new { checks, microsecondsPerCheck = timer.Elapsed.TotalMicroseconds / checks };

File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
Console.WriteLine($"wrote {outPath}");

sealed record Outcome(bool CorrectChanged, string? CorrectResult, bool WrongFixed, string? WrongResult);

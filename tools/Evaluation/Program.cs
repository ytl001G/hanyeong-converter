// Accuracy evaluation for docs/accuracy-report.md.
// Usage: dotnet run -c Release --project tools/Evaluation -- <held-out folder> [results.json] [--quick]
// --quick skips the threshold sweep, Ctrl+Space fidelity and speed: enough to check a detector change.
// --edge <folder> adds edge-case statistics from more Leipzig corpora (kor*/eng* subfolders, any size).
// The folder holds Leipzig corpora not used to build the word tables (see docs/accuracy-report.md):
//   eng_news_2024_100K, eng_wikipedia_2016_100K, kor-kr_web_2015_100K, kor_newscrawl_2016_100K
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HanYeong.Core;

Console.OutputEncoding = Encoding.UTF8;
bool quick = args.Contains("--quick");
int edgeIndex = Array.IndexOf(args, "--edge");
string? edgeRoot = edgeIndex >= 0 ? args[edgeIndex + 1] : null;
var positional = args.Where((a, i) => !a.StartsWith("--") && (edgeIndex < 0 || i != edgeIndex + 1)).ToArray();
string root = positional[0];
string outPath = positional.Length > 1 ? positional[1] : "eval-results.json";
var report = new Dictionary<string, object>();
var clock = Stopwatch.StartNew();
void Lap(string section) { Console.WriteLine($"{section}: {clock.Elapsed.TotalSeconds:F1}s"); clock.Restart(); }

// ---------- load cost ----------
long memBefore = GC.GetTotalMemory(true);
var loadTimer = Stopwatch.StartNew();
_ = WordFrequency.English.Value;
_ = WordFrequency.Korean.Value;
_ = NameList.Default.Value;
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
// Each distinct word once, on all cores: the detector only reads shared tables.
Dictionary<string, Outcome> Evaluate(AutoDetector d, IReadOnlyList<string> distinct)
{
    var results = new Outcome?[distinct.Count];
    Parallel.For(0, distinct.Count, i =>
    {
        string w = distinct[i];
        if (IsLatin(w))
        {
            string? c = d.Suggest(w, w, Layout.English);
            string? r = d.Suggest(LayoutConverter.ToKorean(w), w, Layout.Korean);
            results[i] = new Outcome(c is not null, c, r is not null && r == CaseInsensitiveForm(w), r);
        }
        else if (IsHangul(w))
        {
            string keys = LayoutConverter.ToEnglish(w);
            string? c = d.Suggest(w, keys, Layout.Korean);
            string? r = d.Suggest(keys, keys, Layout.English);
            results[i] = new Outcome(c is not null, c, r == w, r);
        }
    });
    var map = new Dictionary<string, Outcome>(distinct.Count, StringComparer.Ordinal);
    for (int i = 0; i < distinct.Count; i++) if (results[i] is { } o) map[distinct[i]] = o;
    return map;
}

var allWords = sentences.Values.SelectMany(l => l).SelectMany(t => t).ToList();
var distinctWords = allWords.Distinct(StringComparer.Ordinal).ToList();
Lap("load corpora");
var defaults = new AutoDetector();
var outcome = Evaluate(defaults, distinctWords);

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

// Corpora are independent: one per core, in the original order.
report["corpora"] = corpora.AsParallel().AsOrdered().Select(c => (c.Key, Stats: CorpusStats(c.Key, c.Lang, outcome)))
    .ToList().ToDictionary(c => c.Key, c => c.Stats);
Lap("corpus stats");

// ---------- sentence context (AutoDetectorOptions.InContext once a word of the sentence was fixed) ----------
var contextOutcome = Evaluate(new AutoDetector(options: AutoDetectorOptions.InContext), distinctWords);
var stayingOutcome = Evaluate(new AutoDetector(options: AutoDetectorOptions.Staying), distinctWords);
Lap("evaluate words (default + context)");
object ContextStats(string key, Layout lang)
{
    // Wrong mode, whole sentence: a fixed word turns context on, a word left alone (or digits etc.) turns it off.
    long withTarget = 0, fullyPlain = 0, fullyContext = 0;
    foreach (var sent in sentences[key])
    {
        int target = 0, fixedPlain = 0, fixedContext = 0;
        bool ctx = false;
        foreach (string w in sent)
        {
            bool isTarget = lang == Layout.English ? IsLatin(w) : IsHangul(w);
            if (!isTarget || !outcome.ContainsKey(w)) { ctx = false; continue; }
            target++;
            if (outcome[w].WrongFixed) fixedPlain++;
            bool ok = (ctx ? contextOutcome[w] : outcome[w]).WrongFixed;
            if (ok) fixedContext++;
            ctx = ok;
        }
        if (target == 0) continue;
        withTarget++;
        if (fixedPlain == target) fullyPlain++;
        if (fixedContext == target) fullyContext++;
    }
    return new { sentences = withTarget, fullyFixedWithout = (double)fullyPlain / withTarget, fullyFixedWith = (double)fullyContext / withTarget };
}
// Mixed writing: Korean typed in Korean mode, English words in it also typed in Korean mode (and fixed).
// Context then reaches the following Korean words: "hello 내 친구" may turn 내 into so.
object MixedWritingRisk(string key)
{
    long sentencesWithLatin = 0, koreanAfter = 0, fpPlain = 0, fpContext = 0, sentencesHitPlain = 0, sentencesHitContext = 0;
    var examples = new Dictionary<string, int>();
    foreach (var sent in sentences[key])
    {
        if (!sent.Any(IsLatin)) continue;
        sentencesWithLatin++;
        bool ctx = false, hitPlain = false, hitContext = false;
        foreach (string w in sent)
        {
            if (!outcome.ContainsKey(w)) { ctx = false; continue; }
            if (IsLatin(w)) { ctx = (ctx ? contextOutcome[w] : outcome[w]).WrongFixed; continue; }
            koreanAfter++;
            if (outcome[w].CorrectChanged) { fpPlain++; hitPlain = true; }
            var r = ctx ? contextOutcome[w] : outcome[w];
            if (r.CorrectChanged)
            {
                fpContext++; hitContext = true;
                if (ctx && !outcome[w].CorrectChanged) examples[$"{w}→{r.CorrectResult}"] = examples.GetValueOrDefault($"{w}→{r.CorrectResult}") + 1;
            }
            ctx = r.CorrectChanged;
        }
        if (hitPlain) sentencesHitPlain++;
        if (hitContext) sentencesHitContext++;
    }
    return new
    {
        sentencesWithEnglish = sentencesWithLatin,
        koreanWords = koreanAfter,
        falsePositivePerKoreanWordWithout = (double)fpPlain / koreanAfter,
        falsePositivePerKoreanWordWith = (double)fpContext / koreanAfter,
        sentencesWithFalsePositiveWithout = (double)sentencesHitPlain / sentencesWithLatin,
        sentencesWithFalsePositiveWith = (double)sentencesHitContext / sentencesWithLatin,
        topContextErrors = examples.OrderByDescending(kv => kv.Value).Take(20).Select(kv => new { change = kv.Key, count = kv.Value }).ToList(),
    };
}
// The other direction: Korean typed in English mode (fixed, so context starts), with English words in it that
// were typed in English mode on purpose. Context may then turn them Korean: "분석부터 go" → 해.
object EnglishWordsAfterKoreanContext(string key)
{
    long sentencesWithLatin = 0, latinWords = 0, fpPlain = 0, fpContext = 0, hitContext = 0;
    var examples = new Dictionary<string, int>();
    foreach (var sent in sentences[key])
    {
        if (!sent.Any(IsLatin)) continue;
        sentencesWithLatin++;
        bool ctx = false, hit = false;
        foreach (string w in sent)
        {
            if (!outcome.ContainsKey(w)) { ctx = false; continue; }
            if (IsHangul(w)) { ctx = (ctx ? contextOutcome[w] : outcome[w]).WrongFixed; continue; }
            latinWords++;
            if (outcome[w].CorrectChanged) fpPlain++;
            var r = ctx ? contextOutcome[w] : outcome[w];
            if (r.CorrectChanged)
            {
                fpContext++; hit = true;
                if (ctx && !outcome[w].CorrectChanged) examples[$"{w}→{r.CorrectResult}"] = examples.GetValueOrDefault($"{w}→{r.CorrectResult}") + 1;
            }
            ctx = r.CorrectChanged;
        }
        if (hit) hitContext++;
    }
    return new
    {
        sentencesWithEnglish = sentencesWithLatin,
        englishWords = latinWords,
        falsePositivePerEnglishWordWithout = (double)fpPlain / latinWords,
        falsePositivePerEnglishWordWith = (double)fpContext / latinWords,
        sentencesWithFalsePositiveWith = (double)hitContext / sentencesWithLatin,
        topContextErrors = examples.OrderByDescending(kv => kv.Value).Take(15).Select(kv => new { change = kv.Key, count = kv.Value }).ToList(),
    };
}
// Staying: after a sure word left as typed (one even the context leaves), short words of up to three keys are
// judged with AutoDetectorOptions.Staying. Correct typing: fewer false positives; wrong mode: maybe fewer fixes.
var known = new AutoDetector();
object StayingStats(string key, Layout lang)
{
    long words = 0, fpWithout = 0, fpWith = 0, wrongWords = 0, fixedWithout = 0, fixedWith = 0;
    var examples = new Dictionary<string, int>();
    foreach (var sent in sentences[key])
    {
        bool stayCorrect = false, stayWrong = false;
        foreach (string w in sent)
        {
            if (!outcome.TryGetValue(w, out var o)) { stayCorrect = stayWrong = false; continue; }
            bool latin = IsLatin(w);
            string keys = latin ? w : LayoutConverter.ToEnglish(w);
            bool isShort = keys.Length <= 3;
            // Typed as meant.
            words++;
            bool changed = stayCorrect && isShort ? stayingOutcome[w].CorrectChanged : o.CorrectChanged;
            if (o.CorrectChanged) fpWithout++;
            if (changed) fpWith++;
            if (o.CorrectChanged && !changed) examples[w] = examples.GetValueOrDefault(w) + 1;
            stayCorrect = !changed && !contextOutcome[w].CorrectChanged && known.IsKnownWord(w, latin ? Layout.English : Layout.Korean);
            // Typed in the other mode: only the sentence's own language is mistyped.
            if (latin != (lang == Layout.English)) { stayWrong = false; continue; }
            wrongWords++;
            bool isFixed = stayWrong && isShort ? stayingOutcome[w].WrongFixed : o.WrongFixed;
            if (o.WrongFixed) fixedWithout++;
            if (isFixed) fixedWith++;
            string screen = latin ? LayoutConverter.ToKorean(w) : keys;
            stayWrong = !isFixed && !contextOutcome[w].WrongFixed && known.IsKnownWord(screen, latin ? Layout.Korean : Layout.English);
        }
    }
    return new
    {
        falsePositivePerWordWithout = (double)fpWithout / words,
        falsePositivePerWordWith = (double)fpWith / words,
        fixedWithout = (double)fixedWithout / wrongWords,
        fixedWith = (double)fixedWith / wrongWords,
        noLongerChanged = examples.OrderByDescending(kv => kv.Value).Take(15).Select(kv => $"{kv.Key}({kv.Value})").ToList(),
    };
}
report["staying"] = corpora.AsParallel().AsOrdered().Select(c => (c.Key, Stats: StayingStats(c.Key, c.Lang))).ToList().ToDictionary(c => c.Key, c => c.Stats);

var contextParts = new (string Name, Func<object> Run)[]
{
    ("englishAfterKorean_ko_web", () => EnglishWordsAfterKoreanContext("ko_web")),
    ("englishAfterKorean_ko_news", () => EnglishWordsAfterKoreanContext("ko_news")),
    ("en_news", () => ContextStats("en_news", Layout.English)),
    ("en_wiki", () => ContextStats("en_wiki", Layout.English)),
    ("ko_web", () => ContextStats("ko_web", Layout.Korean)),
    ("ko_news", () => ContextStats("ko_news", Layout.Korean)),
    ("mixedWriting_ko_web", () => MixedWritingRisk("ko_web")),
    ("mixedWriting_ko_news", () => MixedWritingRisk("ko_news")),
};
report["context"] = contextParts.AsParallel().AsOrdered().Select(p => (p.Name, Result: p.Run())).ToList().ToDictionary(p => p.Name, p => p.Result);

Lap("context stats");
// ---------- error examples (token frequency weighted) ----------
var freq = new Dictionary<string, int>(outcome.Count, StringComparer.Ordinal);
foreach (string w in allWords) if (outcome.ContainsKey(w)) freq[w] = freq.GetValueOrDefault(w) + 1;
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

Lap("examples");
// ---------- mixed words: English + Korean particle (AI가, SNS를) found in the Korean test text ----------
// Only "word + particle" (SNS를); compounds like LG전자 are a different thing and are not attempted.
var mixedPattern = new System.Text.RegularExpressions.Regex("^[A-Za-z][A-Za-z0-9]+[가-힣]+$");
var mixedAll = sentences["ko_web"].Concat(sentences["ko_news"]).SelectMany(t => t)
    .Where(w => mixedPattern.IsMatch(w)).ToList();
var mixedFreq = mixedAll
    .Where(w => AutoDetector.IsParticleChain(w[w.TakeWhile(char.IsAsciiLetterOrDigit).Count()..]))
    .GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
long mixedTotal = mixedFreq.Values.Sum(v => (long)v), mixedEnFixed = 0, mixedKoFixed = 0;
var mixedMisses = new Dictionary<string, int>();
foreach (var (w, count) in mixedFreq)
{
    int cut = w.TakeWhile(char.IsAsciiLetterOrDigit).Count();
    string keys = w[..cut] + LayoutConverter.ToEnglish(w[cut..]);   // the whole word typed in one mode
    bool en = defaults.CheckMixed(keys, Layout.English) == w;
    bool ko = defaults.CheckMixed(keys, Layout.Korean) == w;
    if (en) mixedEnFixed += count;
    if (ko) mixedKoFixed += count;
    if (!en) mixedMisses[w] = count;
}
// Every Latin + Hangul token (also compounds like LG전자, PGA투어), typed entirely in one mode.
var allMixedFreq = mixedAll.GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
long allMixedEn = 0, allMixedKo = 0;
foreach (var (w, count) in allMixedFreq)
{
    int cut = w.TakeWhile(char.IsAsciiLetterOrDigit).Count();
    string keys = w[..cut] + LayoutConverter.ToEnglish(w[cut..]);
    if (defaults.CheckMixed(keys, Layout.English) == w) allMixedEn += count;
    if (defaults.CheckMixed(keys, Layout.Korean) == w) allMixedKo += count;
}
report["mixed"] = new
{
    allLatinHangulFixedEnglishMode = (double)allMixedEn / mixedAll.Count,
    allLatinHangulFixedKoreanMode = (double)allMixedKo / mixedAll.Count,
    latinHangulTokens = mixedAll.Count,
    distinctWords = mixedFreq.Count,
    tokens = mixedTotal,
    fixedWhenTypedInEnglishMode = (double)mixedEnFixed / mixedTotal,
    fixedWhenTypedInKoreanMode = (double)mixedKoFixed / mixedTotal,
    topMisses = mixedMisses.OrderByDescending(kv => kv.Value).Take(25).Select(kv => new { word = kv.Key, count = kv.Value }).ToList(),
};

Lap("mixed");
// ---------- joined words: power-consumption, don't, and/or ----------
var joinedLatin = new System.Text.RegularExpressions.Regex("^[A-Za-z]+([-/'][A-Za-z]+)+$");
var joinedHangul = new System.Text.RegularExpressions.Regex("^[가-힣]+([-/'][가-힣]+)+$");
object Joined(IEnumerable<string> keys, System.Text.RegularExpressions.Regex pattern, Layout lang)
{
    var counts = keys.SelectMany(k => sentences[k]).SelectMany(t => t).Where(w => pattern.IsMatch(w))
        .GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
    long total = counts.Values.Sum(v => (long)v), changed = 0, fixedCount = 0;
    var fp = new Dictionary<string, int>();
    foreach (var (w, count) in counts)
    {
        // Typed right: must stay. Typed in the other mode: must come back exactly (capitals restored via keys).
        Layout other = lang == Layout.English ? Layout.Korean : Layout.English;
        string typedWrong = lang == Layout.English ? LayoutConverter.ToKorean(w) : LayoutConverter.ToEnglish(w);
        if (defaults.Check(w, lang) is { } c) { changed += count; fp[$"{w}→{c}"] = count; }
        string? r = defaults.Check(typedWrong, other);
        if (r is not null && (lang == Layout.English ? r == CaseInsensitiveForm(w) : r == w)) fixedCount += count;
    }
    return new
    {
        distinctWords = counts.Count,
        tokens = total,
        falsePositive = (double)changed / total,
        fixedWhenTypedInOtherMode = (double)fixedCount / total,
        falsePositiveExamples = fp.OrderByDescending(kv => kv.Value).Take(15).Select(kv => new { change = kv.Key, count = kv.Value }).ToList(),
    };
}
report["joined"] = new
{
    english = Joined(["en_news", "en_wiki"], joinedLatin, Layout.English),
    korean = Joined(["ko_web", "ko_news"], joinedHangul, Layout.Korean),
};

Lap("joined");
// ---------- numbers: 3개, 2024년에 typed in English mode; 3rd, 10kg typed right must stay ----------
var numberKorean = new System.Text.RegularExpressions.Regex("^[0-9]+[가-힣]+$");
var numberLatin = new System.Text.RegularExpressions.Regex("^[0-9]+[A-Za-z]+$");
var koNumbers = sentences["ko_web"].Concat(sentences["ko_news"]).SelectMany(t => t).Where(w => numberKorean.IsMatch(w)).ToList();
var enNumbers = sentences["en_news"].Concat(sentences["en_wiki"]).SelectMany(t => t).Where(w => numberLatin.IsMatch(w)).ToList();
var numberFp = new Dictionary<string, int>();
foreach (string w in enNumbers)
    if (defaults.CheckMixed(w, Layout.English) is { } r) numberFp[$"{w}→{r}"] = numberFp.GetValueOrDefault($"{w}→{r}") + 1;
long numberFixed = koNumbers.LongCount(w =>
{
    int cut = w.TakeWhile(char.IsAsciiDigit).Count();
    return defaults.CheckMixed(w[..cut] + LayoutConverter.ToEnglish(w[cut..]), Layout.English) == w;
});
report["numbers"] = new
{
    koreanTokens = koNumbers.Count,
    fixedWhenTypedInEnglishMode = (double)numberFixed / koNumbers.Count,
    englishTokens = enNumbers.Count,
    falsePositive = (double)numberFp.Values.Sum() / enNumbers.Count,
    falsePositiveExamples = numberFp.OrderByDescending(kv => kv.Value).Take(15).Select(kv => new { change = kv.Key, count = kv.Value }).ToList(),
};

Lap("numbers");
// ---------- failure mining: every word typed in the wrong mode, grouped by shape ----------
// Shape: H = Hangul run, L = Latin run, D = digit run, other characters as they are (LH = SNS를, H(H) = (주)삼성).
static string Shape(string w)
{
    var sb = new StringBuilder();
    char previous = ' ';
    foreach (char c in w)
    {
        char k = Jamo.IsHangul(c) ? 'H' : char.IsAsciiLetter(c) ? 'L' : char.IsAsciiDigit(c) ? 'D' : c;
        if (k != previous || k is not ('H' or 'L' or 'D')) sb.Append(k);
        previous = k;
    }
    return sb.ToString();
}
string? AppResult(string screen, string keys, Layout mode) => defaults.Suggest(screen, keys, mode);
object MineFailures(string[] corpusKeys, Layout lang)
{
    var total = new Dictionary<string, long>();
    var missed = new Dictionary<string, long>();
    var examples = new Dictionary<string, Dictionary<string, int>>();
    var counts = corpusKeys.SelectMany(k => sentences[k]).SelectMany(t => t)
        .Where(w => lang == Layout.Korean ? w.Any(Jamo.IsSyllable) : w.Any(char.IsAsciiLetter))
        .GroupBy(w => w, StringComparer.Ordinal).Select(g => (Word: g.Key, Count: g.Count())).ToArray();
    // Each distinct word judged once, on all cores.
    var ok = new bool[counts.Length];
    Parallel.For(0, counts.Length, i =>
    {
        string w = counts[i].Word;
        if (lang == Layout.Korean)
        {
            // Korean sentence typed in English mode: Hangul became keys, the rest was typed as meant.
            string keys = string.Concat(w.Select(c => Jamo.IsHangul(c) ? LayoutConverter.ToEnglish(c.ToString()) : c.ToString()));
            ok[i] = AppResult(keys, keys, Layout.English) == w;
        }
        else
        {
            // English sentence typed in Korean mode: the screen shows the IME's output, the keys keep capitals.
            string screen = LayoutConverter.ToKorean(w);
            string? r = AppResult(screen, w, Layout.Korean);
            ok[i] = r == w || r == CaseInsensitiveForm(w);
        }
    });
    for (int i = 0; i < counts.Length; i++)
    {
        var (w, count) = counts[i];
        string shape = Shape(w);
        total[shape] = total.GetValueOrDefault(shape) + count;
        if (ok[i]) continue;
        missed[shape] = missed.GetValueOrDefault(shape) + count;
        var ex = examples.TryGetValue(shape, out var e) ? e : examples[shape] = new Dictionary<string, int>();
        ex[w] = count;
    }
    long all = total.Values.Sum();
    return missed.OrderByDescending(kv => kv.Value).Take(25).Select(kv => new
    {
        shape = kv.Key,
        missedTokens = kv.Value,
        shareOfAllWords = (double)kv.Value / all,
        missRateInShape = (double)kv.Value / total[kv.Key],
        examples = examples[kv.Key].OrderByDescending(x => x.Value).Take(12).Select(x => $"{x.Key}({x.Value})").ToList(),
    }).ToList();
}
report["failureShapes"] = new
{
    koreanTypedInEnglishMode = MineFailures(["ko_web", "ko_news"], Layout.Korean),
    englishTypedInKoreanMode = MineFailures(["en_news", "en_wiki"], Layout.English),
};

Lap("failure shapes");
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
if (!quick) {
foreach (float v in new[] { 0f, 0.5f, 1.0f, 1.5f, 2.0f, 3.0f }) sweep.Add(SweepPoint("EnglishToKoreanMargin", v, d0 with { EnglishToKoreanMargin = v }));
foreach (float v in new[] { 0.5f, 1.0f, 1.5f, 2.0f, 2.5f }) sweep.Add(SweepPoint("UnseenKorean", v, d0 with { UnseenKorean = v }));
foreach (float v in new[] { 1.0f, 1.5f, 2.0f, 2.5f, 3.0f }) sweep.Add(SweepPoint("KoreanToEnglishMargin", v, d0 with { KoreanToEnglishMargin = v }));
foreach (float v in new[] { 2.5f, 3.0f, 3.5f, 4.0f, 4.5f }) sweep.Add(SweepPoint("CommonKorean", v, d0 with { CommonKorean = v }));
foreach (float v in new[] { 1.0f, 1.5f, 2.0f, 2.5f, 3.0f }) sweep.Add(SweepPoint("KnownEnglish", v, d0 with { KnownEnglish = v }));
report["sweep"] = sweep;
Lap("threshold sweep");
}

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
// Same as the app: single-word rules, then English + Korean (mixed) rules on the keys.
bool AppConverts(string s, Layout mode) => defaults.Suggest(s, mode == Layout.English ? s : LayoutConverter.ToEnglish(s), mode) is not null;
const int N = 100_000;
report["nonsense"] = new
{
    randomLowercaseInEnglishMode = Rate(N, () => RandomString(lower, 3, 10), s => AppConverts(s, Layout.English)),
    randomLowercaseFormsValidHangul = Rate(N, () => RandomString(lower, 3, 10), s => AutoDetector.IsWellFormed(LayoutConverter.ToKorean(s))),
    randomKeysInKoreanMode = Rate(N, () => LayoutConverter.ToKorean(RandomString(lower, 3, 10)), s => AppConverts(s, Layout.Korean)),
    idLikeLettersDigits = Rate(N, () => RandomString(lower, 3, 8) + RandomString("0123456789", 1, 4), s => AppConverts(s, Layout.English)),
    passwordLike = Rate(N, () => RandomString(lower + lower.ToUpperInvariant() + "0123456789!@#$%", 8, 14), s => AppConverts(s, Layout.English)),
    // Strings that are valid Hangul syllables but (almost surely) not words, typed in English mode.
    randomSyllablesTypedInEnglish = Rate(N, () => LayoutConverter.ToEnglish(new string(Enumerable.Range(0, rng.Next(2, 5)).Select(_ => common[rng.Next(common.Length)]).ToArray())),
        s => AppConverts(s, Layout.English)),
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
if (!quick)
{
    var fidelity = new (string Key, Layout Lang)[] { ("en_news", Layout.English), ("en_wiki", Layout.English), ("ko_web", Layout.Korean), ("ko_news", Layout.Korean) }
        .AsParallel().AsOrdered().Select(c => (c.Key, Result: Fidelity(c.Key, c.Lang))).ToList();
    report["hotkey"] = fidelity.ToDictionary(f => f.Key, f => f.Result);
    Lap("Ctrl+Space fidelity");
}

// ---------- speed ----------
if (!quick) {
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
}

// ---------- edge cases from more corpora: only the words of rare shapes are kept, each judged once ----------
if (edgeRoot is not null)
{
    var heldOut = new HashSet<string>(raw.Values.SelectMany(l => l), StringComparer.Ordinal);
    var mixedShape = new System.Text.RegularExpressions.Regex("^[A-Za-z][A-Za-z0-9]*[가-힣][가-힣A-Za-z0-9]*$");
    var numberShape = new System.Text.RegularExpressions.Regex("^[+-]?[0-9][0-9,.]*[가-힣]+$");
    var acronymShape = new System.Text.RegularExpressions.Regex("^[A-Z]{2,}s?$");
    var camelShape = new System.Text.RegularExpressions.Regex("^([a-z]+[A-Z]|[A-Z][a-z]+[A-Z])[A-Za-z]*$");
    var numberLatinShape = new System.Text.RegularExpressions.Regex("^[0-9]+[A-Za-z]+$");
    const string HangulPunctuation = "“”‘’\"'()[]·-/%~";
    // Hanja (고(故)) cannot be typed in either mode, so such words are left out.
    bool IsPunctHangul(string w) => w.Any(Jamo.IsSyllable) && w.Any(c => HangulPunctuation.Contains(c)) && !w.Any(char.IsAsciiLetter)
        && !w.Any(c => c is >= '\u4E00' and <= '\u9FFF');
    bool IsCodeLike(string w) => w.Contains('@') || w.Contains("://") || w.StartsWith("www.") || (w.Contains('_') && w.Any(char.IsAsciiLetter));

    // category -> word -> count; categories of Korean text start with "ko:", of English text with "en:"
    var cats = new Dictionary<string, Dictionary<string, int>>();
    void Add(string cat, string w)
    {
        var d = cats.TryGetValue(cat, out var x) ? x : cats[cat] = new Dictionary<string, int>(StringComparer.Ordinal);
        d[w] = d.GetValueOrDefault(w) + 1;
    }
    var edgeSentences = new Dictionary<string, long>();
    foreach (string dir in Directory.GetDirectories(edgeRoot).OrderBy(d => d))
    {
        string name = Path.GetFileName(dir);
        bool isKorean = name.StartsWith("kor");
        long kept = 0;
        foreach (string file in Directory.GetFiles(dir, "*-sentences.txt", SearchOption.AllDirectories))
        foreach (string line in File.ReadLines(file))
        {
            int tab = line.IndexOf('\t');
            string sentence = tab >= 0 ? line[(tab + 1)..] : line;
            if (heldOut.Contains(sentence)) continue;
            kept++;
            string[] spaced = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int t = 0; t < spaced.Length; t++)
            {
                string token = spaced[t];
                if (IsCodeLike(token)) { Add(isKorean ? "ko:code" : "en:code", token.TrimEnd('.', ',', ')', ';', ':')); continue; }
                foreach (string w in token.Split(wordEnds, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (isKorean)
                    {
                        if (mixedShape.IsMatch(w)) Add("ko:latinHangul", w);
                        else if (numberShape.IsMatch(w)) Add("ko:numberHangul", w);
                        else if (w.Any(Jamo.IsJamo)) Add("ko:chatJamo", w);
                        else if (IsPunctHangul(w)) Add("ko:punctHangul", w);
                        else if (IsLatin(w)) Add("ko:englishInKorean", w);
                    }
                    else
                    {
                        if (acronymShape.IsMatch(w)) Add("en:acronym", w);
                        else if (camelShape.IsMatch(w)) Add("en:camelCase", w);
                        else if (numberLatinShape.IsMatch(w)) Add("en:numberLatin", w);
                        // A capitalized word inside the sentence that no word list has: a name.
                        else if (t > 0 && IsLatin(w) && char.IsAsciiLetterUpper(w[0]) && w.Skip(1).All(char.IsAsciiLetterLower)
                            && WordFrequency.English.Value.Score(w) < AutoDetectorOptions.Default.KnownEnglish) Add("en:unknownName", w);
                    }
                }
            }
        }
        edgeSentences[name] = kept;
    }
    Lap("edge: scan corpora");

    // Typed as meant: any change is a false positive. A code-like run is split into the app's words at . : , etc.
    bool CorrectChanged(string w, bool isKoreanText)
    {
        if (isKoreanText) return AppResult(w, LayoutConverter.ToEnglish(w), Layout.Korean) is not null;
        // Word by word as the app sees them, with what was typed before each (www.kt.com: www, then kt after "www.").
        int start = 0;
        for (int i = 0; i <= w.Length; i++)
        {
            if (i < w.Length && !wordEnds.Contains(w[i])) continue;
            string piece = w[start..i];
            if (piece.Length > 0 && !AutoDetector.IsInAddress(w[..start]) && AppResult(piece, piece, Layout.English) is not null) return true;
            start = i + 1;
        }
        return false;
    }
    // Typed in the other mode: Korean in English mode (the rest typed as meant), English in Korean mode.
    bool WrongFixed(string w, bool isKoreanText)
    {
        if (isKoreanText)
        {
            string keys = string.Concat(w.Select(c => Jamo.IsHangul(c) ? LayoutConverter.ToEnglish(c.ToString()) : c.ToString()));
            return AppResult(keys, keys, Layout.English) == w;
        }
        string? r = AppResult(LayoutConverter.ToKorean(w), w, Layout.Korean);
        return r == w || r == CaseInsensitiveForm(w);
    }
    // Latin + Hangul typed entirely in Korean mode: the screen shows broken Hangul for the Latin part.
    bool KoreanModeFixed(string w)
    {
        string keys = LayoutConverter.ToEnglish(w);
        string? r = AppResult(LayoutConverter.ToKorean(keys), keys, Layout.Korean);
        return r == w || (r is not null && CaseInsensitiveForm(r) == CaseInsensitiveForm(w));
    }
    // Per category: false positives when typed as meant, recovery when typed in the other mode, all-Korean-mode recovery.
    var measures = new Dictionary<string, (bool Fp, bool Fix, bool KoMode)>
    {
        ["ko:latinHangul"] = (false, true, true),
        ["ko:numberHangul"] = (true, true, false),
        ["ko:punctHangul"] = (true, true, false),
        ["ko:chatJamo"] = (true, false, false),
        ["ko:englishInKorean"] = (true, true, false),
        ["ko:code"] = (true, false, false),
        ["en:unknownName"] = (true, true, false),
        ["en:camelCase"] = (true, true, false),
        ["en:acronym"] = (true, true, false),
        ["en:numberLatin"] = (true, false, false),
        ["en:code"] = (true, false, false),
    };
    var edge = new Dictionary<string, object>();
    foreach (var (cat, words) in cats.OrderBy(c => c.Key))
    {
        var (fpOn, fixOn, koModeOn) = measures[cat];
        bool koText = cat is not ("ko:englishInKorean" or "ko:code") && cat.StartsWith("ko:");
        var list = words.ToArray();
        var fp = new bool[list.Length];
        var fix = new bool[list.Length];
        var ko = new bool[list.Length];
        Parallel.For(0, list.Length, i =>
        {
            string w = list[i].Key;
            if (fpOn) fp[i] = CorrectChanged(w, koText);
            if (fixOn) fix[i] = WrongFixed(w, koText);
            if (koModeOn) ko[i] = KoreanModeFixed(w);
        });
        long total = list.Sum(x => (long)x.Value);
        double EdgeRate(bool[] flags) => (double)list.Where((x, i) => flags[i]).Sum(x => (long)x.Value) / total;
        List<string> EdgeTop(bool[] flags, bool want) => list.Select((x, i) => (x, i)).Where(p => flags[p.i] == want)
            .OrderByDescending(p => p.x.Value).Take(20).Select(p => $"{p.x.Key}({p.x.Value})").ToList();
        edge[cat] = new
        {
            tokens = total,
            distinctWords = list.Length,
            falsePositive = fpOn ? EdgeRate(fp) : (double?)null,
            falsePositiveExamples = fpOn ? EdgeTop(fp, true) : null,
            fixedWhenTypedInOtherMode = fixOn ? EdgeRate(fix) : (double?)null,
            missedExamples = fixOn ? EdgeTop(fix, false) : null,
            fixedWhenTypedAllInKoreanMode = koModeOn ? EdgeRate(ko) : (double?)null,
            missedAllInKoreanModeExamples = koModeOn ? EdgeTop(ko, false) : null,
        };
    }
    report["edgeCases"] = new { sentences = edgeSentences, categories = edge };
    Lap("edge: judge");
}

File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
Console.WriteLine($"wrote {outPath}");

sealed record Outcome(bool CorrectChanged, string? CorrectResult, bool WrongFixed, string? WrongResult);

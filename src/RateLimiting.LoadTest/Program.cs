using System.Diagnostics;
using System.Net;
using System.Text;

// Load test for the rate-limiting-azure API.
//
// Three scenarios, one question each:
//   boundary   How many requests get through when a burst straddles a window boundary?
//   sustained  What happens under 3x the allowed rate for three windows?
//   concurrency How many calls reach a slow backend when 20 arrive at once?
//
// Usage: dotnet run -- [--base http://localhost:5080] [--limit 10] [--window 1000] [--redis] [--out results.md]
//                     [--header "Ocp-Apim-Subscription-Key: <key>"]   (repeatable; for calls through APIM)

var baseUrl = Arg("--base") ?? "http://localhost:5080";
var limit = int.Parse(Arg("--limit") ?? "10");
var windowMs = int.Parse(Arg("--window") ?? "1000");
var includeRedis = args.Contains("--redis");
var outFile = Arg("--out");
var extraHeaders = args.Select((a, i) => (a, i)).Where(t => t.a == "--header" && t.i + 1 < args.Length)
    .Select(t => args[t.i + 1].Split(':', 2)).Where(p => p.Length == 2)
    .Select(p => (name: p[0].Trim(), value: p[1].Trim())).ToList();

// Trailing slash + relative paths, so a base URL with a path segment (APIM: .../ratelimit) is kept.
var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
foreach (var (name, value) in extraHeaders) http.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
var report = new StringBuilder();
var unexpectedReported = 0;

var timeBased = new List<string> { "fixed-window", "sliding-log", "sliding-counter", "token-bucket", "leaky-bucket" };
if (includeRedis)
    timeBased.AddRange(new[] { "redis/fixed-window", "redis/sliding-log", "redis/sliding-counter", "redis/token-bucket" });

Log($"# Load test results\n\nTarget: `{baseUrl}`, limit {limit} per {windowMs} ms, {DateTimeOffset.UtcNow:u}\n");
if (extraHeaders.Count > 0)
    Log($"Extra headers: {string.Join(", ", extraHeaders.Select(h => h.name))}. Through APIM the gateway overrides X-Client-Id with the subscription id, so all scenarios share one partition; run them far enough apart or read the sustained rows only.\n");

// ---------------------------------------------------------------- boundary burst
Log($"## Scenario 1: burst across the window boundary\n");
Log($"1 request opens the window, {limit - 1} more at 90% of the window, {limit} more at 110%. " +
    $"A perfect limiter accepts {limit} in any {windowMs} ms span.\n");
Log("| Algorithm | Sent | 200 | 429 | other | Max accepted in any window | p50 latency | max latency |");
Log("|---|---|---|---|---|---|---|---|");

foreach (var ep in timeBased)
{
    var client = $"burst-{ep.Replace('/', '-')}-{Guid.NewGuid():N}";
    var results = new List<Result>();
    var sw = Stopwatch.StartNew();

    results.Add(await Fire(ep, client));
    await SleepUntil(sw, windowMs * 0.9);
    results.AddRange(await Task.WhenAll(Enumerable.Range(0, limit - 1).Select(_ => Fire(ep, client))));
    await SleepUntil(sw, windowMs * 1.1);
    results.AddRange(await Task.WhenAll(Enumerable.Range(0, limit).Select(_ => Fire(ep, client))));

    Log(Row(ep, results, windowMs));
}

// ---------------------------------------------------------------- sustained overload
var rate = limit * 3;
var seconds = 3;
Log($"\n## Scenario 2: sustained overload at {rate} requests per {windowMs} ms for {seconds} windows\n");
Log($"Uniform arrivals, {rate * seconds} requests total. A perfect limiter accepts about {limit * seconds}.\n");
Log("| Algorithm | Sent | 200 | 429 | other | Max accepted in any window | p50 latency | p95 latency | max latency |");
Log("|---|---|---|---|---|---|---|---|---|");

foreach (var ep in timeBased)
{
    var client = $"sustained-{ep.Replace('/', '-')}-{Guid.NewGuid():N}";
    var interval = windowMs / (double)rate;
    var total = rate * seconds;
    var tasks = new List<Task<Result>>(total);
    var sw = Stopwatch.StartNew();
    for (var i = 0; i < total; i++)
    {
        await SleepUntil(sw, i * interval);
        tasks.Add(Fire(ep, client));
    }
    var results = (await Task.WhenAll(tasks)).ToList();
    Log(Row(ep, results, windowMs, includeP95: true));
}

// ---------------------------------------------------------------- concurrency
var burst = 20;
Log($"\n## Scenario 3: {burst} simultaneous calls to a slow backend\n");
Log("The concurrency endpoint sleeps to simulate a slow dependency. Without a limiter every call reaches it.\n");
Log("| Endpoint | Sent | 200 | 429 | other | Reached backend concurrently | Wall time |");
Log("|---|---|---|---|---|---|---|");

foreach (var ep in new[] { "unlimited", "concurrency" })
{
    var client = $"conc-{ep}-{Guid.NewGuid():N}";
    var sw = Stopwatch.StartNew();
    var results = (await Task.WhenAll(Enumerable.Range(0, burst).Select(_ => Fire(ep, client)))).ToList();
    sw.Stop();
    var ok = results.Count(r => r.Status == HttpStatusCode.OK);
    var rejected = results.Count(r => r.Status == HttpStatusCode.TooManyRequests);
    Log($"| /api/{ep} | {results.Count} | {ok} | {rejected} | {results.Count - ok - rejected} | {ok} | {sw.ElapsedMilliseconds} ms |");
}

Console.WriteLine();
if (outFile is not null)
{
    File.WriteAllText(outFile, report.ToString());
    Console.WriteLine($"Written to {outFile}");
}

// ---------------------------------------------------------------- helpers
async Task<Result> Fire(string endpoint, string clientId)
{
    using var req = new HttpRequestMessage(HttpMethod.Get, $"api/{endpoint}");
    req.Headers.Add("X-Client-Id", clientId);
    var sent = Stopwatch.GetTimestamp();
    using var res = await http.SendAsync(req);
    var done = Stopwatch.GetTimestamp();
    if (res.StatusCode != HttpStatusCode.OK && res.StatusCode != HttpStatusCode.TooManyRequests && Interlocked.Exchange(ref unexpectedReported, 1) == 0)
    {
        var body = await res.Content.ReadAsStringAsync();
        Console.Error.WriteLine($"Unexpected status {(int)res.StatusCode} from {res.RequestMessage?.RequestUri}: {body[..Math.Min(200, body.Length)]}");
    }
    return new Result(res.StatusCode, sent, done);
}

static async Task SleepUntil(Stopwatch sw, double elapsedMs)
{
    var remaining = elapsedMs - sw.Elapsed.TotalMilliseconds;
    if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining));
}

static string Row(string ep, List<Result> results, int windowMs, bool includeP95 = false)
{
    var ok = results.Where(r => r.Status == HttpStatusCode.OK).ToList();
    var rejected = results.Count(r => r.Status == HttpStatusCode.TooManyRequests);
    var latencies = results.Select(r => r.LatencyMs).OrderBy(x => x).ToList();
    var p50 = Percentile(latencies, 0.50);
    var p95 = Percentile(latencies, 0.95);
    var max = latencies.Count == 0 ? 0 : latencies[^1];
    var extra = includeP95 ? $" {p95:F0} ms |" : "";
    return $"| {ep} | {results.Count} | {ok.Count} | {rejected} | {results.Count - ok.Count - rejected} | {MaxInAnyWindow(ok, windowMs)} | {p50:F0} ms |{extra} {max:F0} ms |";
}

/// The number that matters: the most requests the backend saw inside ANY span of one window,
/// measured on the response timestamp (when the request actually got through).
static int MaxInAnyWindow(List<Result> accepted, int windowMs)
{
    if (accepted.Count == 0) return 0;
    var times = accepted.Select(r => r.Done).OrderBy(t => t).ToArray();
    var windowTicks = (long)(windowMs / 1000.0 * Stopwatch.Frequency);
    int best = 0, left = 0;
    for (var right = 0; right < times.Length; right++)
    {
        while (times[right] - times[left] > windowTicks) left++;
        best = Math.Max(best, right - left + 1);
    }
    return best;
}

static double Percentile(List<double> sorted, double p)
{
    if (sorted.Count == 0) return 0;
    var idx = (int)Math.Ceiling(p * sorted.Count) - 1;
    return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
}

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

void Log(string line)
{
    Console.WriteLine(line);
    report.AppendLine(line);
}

record Result(HttpStatusCode Status, long Sent, long Done)
{
    public double LatencyMs => (Done - Sent) * 1000.0 / Stopwatch.Frequency;
}

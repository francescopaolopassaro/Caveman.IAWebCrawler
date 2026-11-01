namespace Caveman.IAWebCrawler;

/// <summary>
 /// Minimal robots.txt parser: groups by user-agent ("caveman" or "*"),
 /// Disallow/Allow/Sitemap/Crawl-delay rules with prefix matching (no wildcards).
/// </summary>
public sealed class RobotsEvaluator
{
    private readonly List<string> _disallow = new();
    private readonly List<string> _allow = new();
    private readonly List<string> _sitemaps = new();

    public string Raw { get; private set; } = "";
    public int CrawlDelayMilliseconds { get; private set; }

    public static async Task<RobotsEvaluator> FetchAsync(
        HttpClient http, Uri siteRoot, string userAgent, CancellationToken ct = default)
    {
        var robotsUrl = new Uri(siteRoot, "/robots.txt").ToString();
        var eval = new RobotsEvaluator();
        try
        {
            using var resp = await http.GetAsync(robotsUrl, ct);
            if (!resp.IsSuccessStatusCode)
                return eval; // senza robots.txt: tutto consentito
            var text = await resp.Content.ReadAsStringAsync(ct);
            eval.Parse(text);
        }
        catch
        {
             // network error on robots.txt: allowed by default
        }
        return eval;
    }

    internal void Parse(string text)
    {
        Raw = text;

         // groups: each block "User-agent: ... / Disallow: ...".
         // we take the rules of the first group matching our UA, otherwise "*".
        List<string>? chosenRules = null;
        bool chosenIsStar = false;

        var currentUas = new List<string>();
        var currentRules = new List<string>();

        void Flush()
        {
            if (currentUas.Count == 0) return;
            bool match = currentUas.Any(u => u.Contains("caveman", StringComparison.OrdinalIgnoreCase));
            bool star = currentUas.Any(u => u.Trim() == "*");
            bool chosenIsNull = chosenRules == null;
            bool canReplace = chosenIsNull || (chosenIsStar && !star && match);
            if (match && canReplace) { chosenRules = currentRules; chosenIsStar = false; }
            else if (star && chosenRules == null) { chosenRules = currentRules; chosenIsStar = true; }
            currentUas.Clear();
            currentRules.Clear();
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            if (key == "user-agent")
            {
                if (currentRules.Count > 0) Flush();
                currentUas.Add(value);
            }
            else if (key is "disallow" or "allow" or "crawl-delay" or "sitemap")
            {
                if (key == "sitemap")
                {
                    _sitemaps.Add(value);
                    continue;
                }
                if (key == "crawl-delay")
                {
                    if (int.TryParse(value.Split('.')[0], out var secs))
                        CrawlDelayMilliseconds = Math.Max(CrawlDelayMilliseconds, secs * 1000);
                    continue;
                }
                if (currentUas.Count > 0) currentRules.Add($"{key}[=]{value}");
            }
        }
        Flush();

        foreach (var rule in chosenRules ?? new List<string>())
        {
            var sep = rule.IndexOf("[=]", StringComparison.Ordinal);
            if (sep < 0) continue;
            var key = rule[..sep];
            var value = rule[(sep + 3)..];
            if (key == "disallow" && value.Length > 0) _disallow.Add(value);
            else if (key == "allow" && value.Length > 0) _allow.Add(value);
        }
    }

     /// <summary>True when the path and query are allowed by robots.txt.</summary>
    public bool IsAllowed(Uri url)
    {
        var pathAndQuery = url.PathAndQuery;
        var bestAllow = LongestMatch(_allow, pathAndQuery);
        var bestDisallow = LongestMatch(_disallow, pathAndQuery);
         // the longest rule wins; on a tie Allow wins
        return bestAllow.Length >= bestDisallow.Length;
    }

    private static string LongestMatch(IEnumerable<string> prefixes, string path)
    {
        string best = "";
        foreach (var p in prefixes)
            if (p.Length > best.Length && path.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                best = p;
        return best;
    }

    public IReadOnlyList<string> Sitemaps => _sitemaps;
}

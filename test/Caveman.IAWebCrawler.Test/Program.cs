using Caveman.IAWebCrawler;

// =====================================================================
// Test project for Caveman.IAWebCrawler
//
// Usage:
//   dotnet run --project test/Caveman.IAWebCrawler.Test [url] [maxPages] [mode] [outputFolder] [depth]
//                                             [--identity=...] [--language=...] [--cookie=n=v;n2=v2] [--header=n=v;n2=v2]
//
//   mode:        json | markdown | files | all   (default: all)
//   depth:       link level to follow            (default: 5; -1 = unlimited)
//   identity:    browser profile among the 20 available: chrome|edge|firefox|brave (windows/mac/linux),
//                safari (mac/ios/ipad), chromium (linux), samsung (android), mobile (android chrome),
//                mobile-tablet, edge-firefox-safari-ios... (default: chrome = Chrome on Windows)
//   language:    among the 40 supported: it, en-US, en-GB, de, fr, es, pt, pt-BR, nl, ru, pl, uk, cs, sk,
//                hu, ro, bg, el, hr, sl, sr, tr, da, sv, nb, fi, ar, he, fa, hi, ur, bn, zh-CN, zh-TW,
//                ja, ko, th, vi, id, ms  (or a full Accept-Language string)
//   cookie:      starting cookies "name=value; name2=value2"
//   header:      extra headers "Name=value; Name2=value2"
//
// Separate extraction per output type:
//   -- json      -> json/results.json (extracted information)
//   -- markdown  -> markdown/*.md + rag/ (AI content; documents and images inside the RAG)
//   -- files     -> files/<host>/... (raw archive: html js css pdf docx csv json txt + recursive fonts)
// =====================================================================

// --- positional arguments (url, pages, mode, folder, depth) ---
var positional = new List<string>();
string identity = "chrome", language = "it", cookie = "", header = "";
foreach (var raw in args)
{
    if (raw.StartsWith("--", StringComparison.Ordinal))
    {
        var kv = raw[2..].Split('=', 2);
        var val = kv.Length > 1 ? kv[1] : "";
        switch (kv[0].ToLowerInvariant())
        {
            case "identity": case "identita": identity = val.ToLowerInvariant(); break;
            case "language": case "lingua":   language = val; break;
            case "cookie":    cookie = val; break;
            case "header":    header = val; break;
            default: Console.WriteLine($"? unrecognized option: --{kv[0]}"); break;
        }
    }
    else positional.Add(raw);
}

var startUrl  = positional.Count > 0 ? positional[0] : "https://www.comune.silea.tv.it/";
var maxPages  = positional.Count > 1 && int.TryParse(positional[1], out var mp) ? mp : 40;
var modeName  = positional.Count > 2 ? positional[2].ToLowerInvariant() : "all";
var outputDir = positional.Count > 3 ? positional[3] : "output";
var maxDepth  = positional.Count > 4 && int.TryParse(positional[4], out var md) ? md : 5;

var modes = modeName switch
{
    "json"     => CrawlModes.Json,
    "markdown" => CrawlModes.Markdown,
    "files"    => CrawlModes.Files,
    _          => CrawlModes.All
};

var options = new CrawlerOptions
{
    StartUrl = startUrl,
    Modes = modes,
    OutputDirectory = outputDir,
    MaxPages = maxPages,
    MaxDepth = maxDepth,             // adjustable link depth (default 5)

    // --- browser identity to avoid being blocked by the site ---
    BrowserIdentity = Browsers.TryParseKind(identity, out var kind)
        ? kind : BrowserKind.ChromeWindows,
    // language: short code ("it"), BCP-47 ("it-IT") or a full header string
    AcceptLanguage = AcceptLanguages.HeaderFor(language) ?? language,
    DelayMilliseconds = 300,         // courtesy towards the municipal server
    RespectRobotsTxt = true,
    DownloadAssets = true,           // css, js, images, fonts (external CDNs included)
    DownloadDocuments = true,        // pdf, docx, csv, json, txt...
    // the raw files/ copy is written only when the requested output needs it
    SaveRawResponse = modes.HasFlag(CrawlModes.Files),
    SaveAssetsForMarkdown = !modes.HasFlag(CrawlModes.Files),
    CombinedMarkdownFile = true,
    Log = msg => Console.WriteLine(msg)
};

// extra cookies and headers: "name=value; name2=value2"
foreach (var (name, value) in ParsePairs(cookie))
    options.Cookies[name] = value;
foreach (var (name, value) in ParsePairs(header))
    options.ExtraHeaders[name] = value;

static IEnumerable<(string Name, string Value)> ParsePairs(string spec)
{
    foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
    {
        var kv = part.Split('=', 2);
        if (kv.Length == 2 && kv[0].Trim().Length > 0)
            yield return (kv[0].Trim(), kv[1].Trim());
    }
}

using var crawler = new CavemanWebCrawler(options);

Console.OutputEncoding = System.Text.Encoding.UTF8;
var result = await crawler.CrawlAsync();

Console.WriteLine();
Console.WriteLine("========================================");
Console.WriteLine($"SUMMARY ({modes})");
Console.WriteLine("========================================");
Console.WriteLine($"Site:          {result.Host}");
Console.WriteLine($"Duration:      {result.Duration.TotalSeconds:F1}s");
Console.WriteLine($"Pages:         {result.Stats.PagesCrawled}");
Console.WriteLine($"Files:         {result.Stats.FilesDownloaded}");
Console.WriteLine($"Robot skip:    {result.Stats.SkippedByRobots}");
Console.WriteLine($"Errors:        {result.Stats.Errors}");
Console.WriteLine($"Total bytes:   {result.Stats.TotalBytes:N0}");

var byType = result.Resources
    .Where(r => r.Error is null)
    .GroupBy(r => r.Type)
    .OrderByDescending(g => g.Count());
foreach (var g in byType)
    Console.WriteLine($"   {g.Key,-11} {g.Count(),5}  {g.Sum(r => r.LengthBytes) / 1024.0,8:F0} KB");

var docs = result.Resources
    .Where(r => r.Type == ResourceType.Document && r.Error is null)
    .Select(r => Path.GetFileName(new Uri(r.Url).AbsolutePath))
    .Where(n => n.Length > 0)
    .Take(15)
    .ToList();
if (docs.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Documents encountered (first 15):");
    foreach (var d in docs) Console.WriteLine($"   {d}");
}

Console.WriteLine();
Console.WriteLine("Output in: " + Path.GetFullPath(outputDir));

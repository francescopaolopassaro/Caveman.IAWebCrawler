using System.Text;

namespace Caveman.IAWebCrawler.Output;

/// <summary>
/// Genera <c>llms.txt</c> e <c>llms-full.txt</c> secondo la convenzione
 /// https://llmstxt.org: essential index of the site + a full version
 /// with all the content in Markdown, ready to be loaded in an LLM context.
/// </summary>
public static class LlmsTxtWriter
{
    public static async Task WriteAsync(SiteCrawlResult result, string outputDirectory, CancellationToken ct = default)
    {
        var dir = Path.Combine(outputDirectory, "markdown");
        Directory.CreateDirectory(dir);

        var pages = result.Pages.Where(p => p.Error is null).ToList();
        var home = pages.FirstOrDefault(p => new Uri(p.Url).AbsolutePath is "/" or "/index.html" or "/home.html")
                   ?? pages.First();
        var description = home.MetaTags.TryGetValue("description", out var d) ? d : "";

        // ---- llms.txt ------------------------------------------------
        var sb = new StringBuilder();
        sb.AppendLine($"# {SiteName(result, home)}");
        sb.AppendLine();
        if (description.Length > 0)
        {
            sb.AppendLine($"> {description}");
            sb.AppendLine();
        }
        sb.AppendLine($"Institutional site or web: {result.StartUrl}. " +
                      $"Content extracted on {result.FinishedAtUtc:yyyy-MM-dd} by Caveman.IAWebCrawler " +
                      $"({result.Stats.PagesCrawled} pages).");
        sb.AppendLine();

        sb.AppendLine("## Page index");
        sb.AppendLine();
        foreach (var g in GroupPages(pages))
        {
            sb.AppendLine($"### {g.Key}");
            sb.AppendLine();
            foreach (var page in g.Value.Take(40))
                sb.AppendLine($"- [{Title(page)}]({page.Url}){(Desc(page) is { } dd ? $": {dd}" : "")}");
            if (g.Value.Count > 40)
                sb.AppendLine($"- … altre {g.Value.Count - 40} pagine");
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(Path.Combine(dir, "llms.txt"), sb.ToString(), ct);

        // ---- llms-full.txt -------------------------------------------
        var full = new StringBuilder();
        full.AppendLine($"# {SiteName(result, home)} — contenuto completo");
        full.AppendLine();
        if (description.Length > 0) full.AppendLine($"> {description}").AppendLine();
        foreach (var page in pages)
        {
            full.AppendLine();
            full.AppendLine($"---");
            full.AppendLine();
            full.AppendLine($"## {Title(page)}");
            full.AppendLine();
            full.AppendLine($"URL: {page.Url}");
            full.AppendLine();
            var body = string.IsNullOrWhiteSpace(page.Markdown) ? string.Join("\n\n", page.Paragraphs) : page.Markdown;
            full.AppendLine(body.Trim());
            if (page.MetaTags.TryGetValue("description", out var pd) && pd.Length > 0)
            {
                full.AppendLine();
                full.AppendLine($"*Descrizione: {pd}*");
            }
        }

        await File.WriteAllTextAsync(Path.Combine(dir, "llms-full.txt"), full.ToString(), ct);
        Console.WriteLine($"  → llms.txt / llms-full.txt: {Path.GetFullPath(dir)}");
    }

    private static string SiteName(SiteCrawlResult result, PageResult home)
    {
        var og = home.MetaTags.FirstOrDefault(m => m.Key is "og:site_name" or "twitter:site").Value;
        return !string.IsNullOrWhiteSpace(og) ? og : result.Host;
    }

    private static string Title(PageResult p) => string.IsNullOrWhiteSpace(p.Title) ? p.Url : p.Title.Trim();

    private static string? Desc(PageResult p) =>
        p.MetaTags.TryGetValue("description", out var v) && v.Length > 0 ? Shorten(v) : null;

    private static string Shorten(string s) => s.Length <= 120 ? s.Trim() : s[..117].TrimEnd() + "…";

     /// <summary>Groups pages by first path segment (e.g. "administration", "news").</summary>
    private static IEnumerable<KeyValuePair<string, List<PageResult>>> GroupPages(List<PageResult> pages)
    {
        if (pages.Count == 0) return Enumerable.Empty<KeyValuePair<string, List<PageResult>>>();
        return pages
            .GroupBy(p =>
            {
                var segs = new Uri(p.Url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                return segs.Length > 0 && segs[0].Length < 40
                    ? segs[0]
                    : "radice";
            })
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList());
    }
}

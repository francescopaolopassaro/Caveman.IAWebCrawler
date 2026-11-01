using System.Text;
using System.Text.RegularExpressions;

namespace Caveman.IAWebCrawler.Output;

/// <summary>
/// Writes the site content as Markdown for AI models — a self-contained
/// «snapshot» of the site:
/// <list type="bullet">
/// <item>one .md per page (with front matter + provenance), an index.md and a combined
///       site-*.md file holding every page;</item>
/// <item>body links pointing to <b>imported pages</b> are rewritten to their local .md
///       files (original source kept as tooltip), images are rewritten to local
///       copies (files/ or assets/) — no live-site references in the snapshot;</item>
/// <item>imported pages (teasers like "read more" fetched at the end of the crawl)
///       are listed in the index too.</item>
/// </list>
/// </summary>
public static partial class MarkdownOutputWriter
{
    public static async Task WriteAsync(
        SiteCrawlResult result, string outputDirectory, bool combinedFile, CancellationToken ct = default)
    {
        var dir = Path.Combine(outputDirectory, "markdown");
        Directory.CreateDirectory(dir);

        // relative archive root to resolve references from inside the markdown tree
        var saveRaw = Path.Combine(outputDirectory, "files");
        var assetsRoot = Path.Combine(outputDirectory, "assets");
        bool archived = Directory.Exists(saveRaw);
        string binaryRoot = archived ? saveRaw : assetsRoot;

        // --- local-URL maps --------------------------------------------------
        var pageNums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in result.Pages.Where(p => p.Error is null))
            pageNums.TryAdd(NormUrl(p.Url), LocalMarkdownPath(p.Url));

        var filePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in result.Resources)
        {
            if (r.Error is not null || r.LocalPath.Length == 0) continue;
            var full = Path.Combine(binaryRoot, r.LocalPath);
            if (!File.Exists(full)) continue;
            filePaths.TryAdd(NormUrl(r.Url), r.LocalPath);
        }

        var ctx = new RewriteContext(dir, archived ? "files" : "assets", pageNums, filePaths);

        var sbIndex = new StringBuilder();
        sbIndex.AppendLine($"# Site index: {result.Host}");
        sbIndex.AppendLine();
        sbIndex.AppendLine($"- **Start URL:** {result.StartUrl}");
        sbIndex.AppendLine($"- **Crawl:** {result.StartedAtUtc:yyyy-MM-dd HH:mm} UTC → " +
                            $"{result.FinishedAtUtc:HH:mm:ss} UTC ({result.Duration.TotalSeconds:F0}s)");
        sbIndex.AppendLine($"- **Pages:** {result.Stats.PagesCrawled} | **Files downloaded:** {result.Stats.FilesDownloaded} | " +
                            $"**Errors:** {result.Stats.Errors} | **Total bytes:** {result.Stats.TotalBytes}");
        sbIndex.AppendLine();
        sbIndex.AppendLine("## Pages");
        sbIndex.AppendLine();

        var imported = new List<PageResult>();
        var combined = new StringBuilder();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var page in result.Pages.Where(p => p.Error is null))
        {
            var relative = LocalMarkdownPath(page.Url);
            var path = Path.Combine(dir, relative);
            if (written.Add(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, BuildPageMarkdown(page, ctx), ct);
            }

            var title = string.IsNullOrWhiteSpace(page.Title) ? page.Url : page.Title;
            if (page.Imported)
            {
                imported.Add(page);
            }
            else
            {
                // "./" prefix: unambiguous relative reference to the local .md file
                var link = relative.StartsWith('.') ? relative : "./" + relative;
                sbIndex.AppendLine($"- [{Escape(title)}]({link}) — source: `{page.Url}`");
                if (combinedFile)
                {
                    combined.AppendLine();
                    combined.AppendLine("---");
                    combined.AppendLine();
                    combined.AppendLine($"<!-- origin: {page.Url} -->");
                    combined.Append(BuildPageMarkdown(page, ctx));
                }
            }
        }

        // imported pages (snapshot expansion) keep their own section in the index
        if (imported.Count > 0)
        {
            sbIndex.AppendLine();
            sbIndex.AppendLine($"## Imported pages ({imported.Count}) — linked bodies completed during the snapshot");
            sbIndex.AppendLine();
            foreach (var page in imported)
            {
                var relative = LocalMarkdownPath(page.Url);
                var title = string.IsNullOrWhiteSpace(page.Title) ? page.Url : page.Title;
                // "./" prefix: unambiguous relative reference to the local .md file
                var link = relative.StartsWith('.') ? relative : "./" + relative;
                sbIndex.AppendLine($"- [{Escape(title)}]({link}) — source: `{page.Url}`");
                if (combinedFile)
                {
                    combined.AppendLine();
                    combined.AppendLine("---");
                    combined.AppendLine();
                    combined.AppendLine($"<!-- origin: {page.Url} (imported during the snapshot) -->");
                    combined.Append(BuildPageMarkdown(page, ctx));
                }
            }
        }

        await File.WriteAllTextAsync(Path.Combine(dir, "index.md"), sbIndex.ToString(), ct);
        Console.WriteLine($"  → Markdown: {Path.GetFullPath(dir)} ({written.Count} pages + index.md)");

        if (combinedFile)
        {
            var combinedPath = Path.Combine(dir, $"site-{Sanitize(result.Host)}.md");
            await File.WriteAllTextAsync(combinedPath, combined.ToString(), ct);
            Console.WriteLine($"  → Combined Markdown (for AI): {combinedPath}");
        }
    }

    // --------------------------------------------------------- self-contained

    /// <summary>Rewriting context shared across all the pages of one run.</summary>
    private sealed record RewriteContext(
        string MarkdownRoot,
        string ArchiveFolder,
        Dictionary<string, string> Pages,
        Dictionary<string, string> Files);

    private static string BuildPageMarkdown(PageResult page, RewriteContext ctx)
    {
        // directory of the .md file being written: relative links start from here
        var sourceDir = Path.GetDirectoryName(Path.Combine(ctx.MarkdownRoot, LocalMarkdownPath(page.Url)))!;
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"url: \"{Escape(page.Url)}\"");
        sb.AppendLine($"title: \"{Escape(page.Title)}\"");
        sb.AppendLine($"crawled: \"{page.CrawledAtUtc:yyyy-MM-ddTHH:mm:ssZ}\"");
        if (page.Imported) sb.AppendLine("imported: \"true\"  # fetched to complete the snapshot (teaser body)");
        if (page.DiscoveredFrom is { Length: > 0 }) sb.AppendLine($"discovered_from: \"{Escape(page.DiscoveredFrom)}\"");
        if (page.Language is not null) sb.AppendLine($"language: \"{Escape(page.Language)}\"");
        if (page.Canonical is not null) sb.AppendLine($"canonical: \"{Escape(page.Canonical)}\"");
        if (page.MetaTags.TryGetValue("description", out var desc) && desc.Length > 0)
            sb.AppendLine($"description: \"{Escape(desc)}\"");
        sb.AppendLine($"status: {page.StatusCode}");
        sb.AppendLine("---");
        sb.AppendLine();

        var body = string.IsNullOrWhiteSpace(page.Markdown)
            ? string.Join("\n\n", page.Paragraphs)
            : page.Markdown.Trim();

        if (body.Length > 0)
            sb.AppendLine(LocalizeBody(body, page, ctx, sourceDir)).AppendLine();

        var interno = page.Links.Where(l => l.Internal).ToList();
        var esterni = page.Links.Where(l => !l.Internal).ToList();

        if (interno.Count > 0 || esterni.Count > 0 || page.Images.Count > 0)
        {
            sb.AppendLine("---").AppendLine();

            if (interno.Count > 0)
            {
                sb.AppendLine("**Internal links:**").AppendLine();
                foreach (var l in interno.Take(50))
                {
                    var (target, source) = ResolveLocal(l.Href, page.Url, sourceDir, ctx);
                    var text = LocalizeLabel(Label(l));
                    sb.AppendLine(target is null
                        ? $"- [{text}]({l.Href})"
                        : $"- [{text}]({target}{SourceTitle(source, l.Href)})");
                }
                if (interno.Count > 50) sb.AppendLine($"- … and {interno.Count - 50} more");
                sb.AppendLine();
            }
            if (esterni.Count > 0)
            {
                sb.AppendLine("**External links / contacts:**").AppendLine();
                foreach (var l in esterni.Take(30))
                    sb.AppendLine($"- [{Label(l)}]({l.Href})");
                sb.AppendLine();
            }
            if (page.Images.Count > 0)
            {
                sb.AppendLine("**Images:**").AppendLine();
                foreach (var i in page.Images)
                {
                    var (target, source) = ResolveLocal(i.Src, page.Url, sourceDir, ctx);
                    var alt = string.IsNullOrWhiteSpace(i.Alt) ? Path.GetFileName(i.Src) : i.Alt;
                    // data-URI images are already inline: nothing to download, nothing to rewrite
                    if (i.Embedded || (target is null && i.Src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)))
                        sb.AppendLine($"- [{alt}]({{inline base64 data URI}})");
                    else
                        sb.AppendLine($"- [{alt}]({target ?? i.Src}{SourceTitle(source, i.Src)})");
                }
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd() + "\n";

        static string Label(LinkInfo l) => string.IsNullOrWhiteSpace(l.Text) ? l.Href : l.Text;
    }

    /// <summary>Tooltip fragment exposing the original source when the reference was localized.</summary>
    private static string SourceTitle(string? source, string url) =>
        string.IsNullOrEmpty(source) ? "" : $" \"source: {source}\"";

    /// <summary>
    /// Rewrites every reference in the page body:
    /// <list type="bullet">
    /// <item>page links → local .md file with the original source as tooltip
    ///       (`[text](page.md "source: https://…")`); unchanged when the target
    ///       has no local copy;</item>
    /// <item>images → local copy when available (tooltip with the source URL);
    ///       inline data-URIs keep their original compact value.</item>
    /// </list>
    /// External references (other domains) stay absolute: they are legitimate
    /// information sources, not parts of this site.
    /// </summary>
    private static string LocalizeBody(string body, PageResult page, RewriteContext ctx, string sourceDir)
        => LinkRegex().Replace(body, m =>
        {
            var text = m.Groups["text"].Value;
            var url = m.Groups["url"].Value.Trim();
            var bang = m.Groups["bang"].Value.Length > 0;

            // inline base64 image: already inside the page, keep it verbatim
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return m.Value;

            var (target, source) = ResolveLocal(url, page.Url, sourceDir, ctx);
            if (bang)
            {
                // image without a local copy: keep the original absolute URL
                return target is null ? m.Value : $"![{text}]({target}{SourceTitle(source, url)})";
            }

            if (target is null) return m.Value;
            return $"[{text}]({target}{SourceTitle(source, url)})";
        });

    /// <summary>
    /// Maps an absolute URL to (a) the relative .md path of a crawled/imported
    /// page, (b) the relative path of a local archive copy, or (c) null.
    /// Paths are expressed from <paramref name="sourceDir"/>, the directory of
    /// the .md file being written.
    /// </summary>
    private static (string? Local, string? Source) ResolveLocal(
        string url, string pageUrl, string sourceDir, RewriteContext ctx)
    {
        var t = (url ?? "").Trim();
        if (t.Length == 0 || t.StartsWith('#')) return (t, null);

        // compute the absolute form (relative hrefs resolve against the page)
        Uri abs;
        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageBase) &&
            Uri.TryCreate(pageBase, t, out var resolved))
            abs = resolved;
        else if (Uri.TryCreate(t, UriKind.Absolute, out var direct))
            abs = direct;
        else
            return (t, null);

        // anchors are meaningless in the md tree: use the page without the fragment
        var key = NormUrl(abs.ToString());
        var sourceUrl = new Uri(abs.GetLeftPart(UriPartial.Query)).ToString();

        if (ctx.Pages.TryGetValue(key, out var relMd))
        {
            var target = Path.Combine(ctx.MarkdownRoot, relMd);
            return (RelativeLink(sourceDir, target), sourceUrl);
        }

        if (ctx.Files.TryGetValue(key, out var relFile))
        {
            var target = Path.Combine(ctx.MarkdownRoot, "..", ctx.ArchiveFolder, relFile);
            return (RelativeLink(sourceDir, target), sourceUrl);
        }

        return (null, sourceUrl);
    }

    /// <summary>Markdown-friendly relative path with a ./ prefix when short.</summary>
    private static string RelativeLink(string fromDir, string toFile)
    {
        var rel = Path.GetRelativePath(fromDir, toFile).Replace('\\', '/');
        return rel.StartsWith('.') ? rel : "./" + rel;
    }

    /// <summary>Cleans stray markers of CMS copy artifacts ("{x}}");</summary>
    private static string LocalizeLabel(string label) =>
        label.Replace("}", "").Replace("{", "").Trim();

    private static string NormUrl(string url)
    {
        try { return new Uri(url).GetLeftPart(UriPartial.Query).ToString(); }
        catch { return url; }
    }

    /// <summary>Converts the page URL into the relative path of its .md file, following the site tree.</summary>
    private static string LocalMarkdownPath(string url)
    {
        var uri = new Uri(url);
        var segments = new List<string> { uri.Host };
        var path = Uri.UnescapeDataString(uri.AbsolutePath);

        var querySuffix = "";
        if (uri.Query.Length > 0)
        {
            var h = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(uri.Query));
            querySuffix = "_" + Convert.ToHexString(h, 0, 4).ToLowerInvariant();
        }

        if (path.EndsWith('/'))
        {
            AddDirectoryPath(segments, path);
            segments.Add("index" + querySuffix + ".md");
        }
        else
        {
            var fileName = Path.GetFileName(path);
            var ext = Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(ext))
            {
                AddDirectoryPath(segments, path.EndsWith('/') ? path : path + "/");
                segments.Add("index" + querySuffix + ".md");
            }
            else
            {
                AddDirectoryPath(segments, Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "");
                segments.Add(Path.GetFileNameWithoutExtension(fileName) + querySuffix + ".md");
            }
        }

        // forward slashes: this value is used in the markdown links too
        return Path.Combine(segments.Select(SanitizeSegment).ToArray()).Replace('\\', '/');
    }

    private static void AddDirectoryPath(List<string> segments, string dirPath)
    {
        foreach (var seg in dirPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            segments.Add(seg);
    }

    private static string SanitizeSegment(string s) => s.Replace("..", "_").Replace(':', '_');

    private static string Escape(string s) =>
        (s ?? "").Replace("\"", "'").Replace("\n", " ").Trim();

    private static string Sanitize(string s) =>
        Regex.Replace(s, "[^a-zA-Z0-9.-]", "_");

    /// <summary>[text](url) or ![text](url) in the markdown body.</summary>
    [GeneratedRegex(@"(!?)\[(?<text>[^\]]*)\]\((?<url>[^)\s]+)\)")]
    private static partial Regex LinkRegex();
}

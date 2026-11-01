using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Caveman.IAWebCrawler.Output;

/// <summary>
/// Post-processing of the files/ archive: rewrites references (href, src, srcset,
/// action, url() and @import in CSS) to the matching archive files, so the
/// downloaded site is browsable locally. References to resources that were not
/// saved stay absolute.
/// </summary>
public static partial class LinkRewriter
{
    /// <summary>Mutable counter shared by the rewriter helpers (lambda-safe).</summary>
    private sealed class Counter { public int Rewritten; }

    /// <summary>Rewrites the links inside the saved HTML/CSS files of the archive.</summary>
    public static void Rewrite(List<PageResult> pages, List<ResourceInfo> resources,
        string outputDirectory, Action<string>? log = null)
    {
        var filesRoot = Path.Combine(outputDirectory, "files");
        if (!Directory.Exists(filesRoot))
        {
            log?.Invoke("  LinkRewriter: no files/ folder to rewrite");
            return;
        }

        // URL (normalized) -> local path relative to files/
        var urlMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in resources)
        {
            if (r.Error is not null || r.LocalPath.Length == 0) continue;
            urlMap[NormUrl(r.Url)] = r.LocalPath;
        }

        // crawled pages (raw copies) also live in files/: map them by URL so
        // page-to-page links keep working inside the archive
        foreach (var page in pages)
        {
            if (page.Error is not null) continue;
            if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var u)) continue;

            var candidate = PageCandidatePath(u);
            if (candidate is null) continue;
            var full = Path.Combine(filesRoot, candidate);
            if (!File.Exists(full)) continue;

            urlMap[NormUrl(page.Url)] = candidate;
        }

        int htmlDone = 0, cssDone = 0;
        var counter = new Counter();

        foreach (var r in resources)
        {
            if (r.Error is not null || r.LocalPath.Length == 0) continue;
            var full = Path.Combine(filesRoot, r.LocalPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) continue;

            var ext = Path.GetExtension(full).ToLowerInvariant();
            try
            {
                if (ext is ".html" or ".htm")
                {
                    counter.Rewritten = 0;
                    RewriteHtml(full, r.Url, filesRoot, urlMap, counter);
                    htmlDone += counter.Rewritten;
                }
                else if (ext == ".css")
                {
                    counter.Rewritten = 0;
                    RewriteCss(full, r.Url, filesRoot, urlMap, counter);
                    cssDone += counter.Rewritten;
                }
            }
            catch (Exception)
            {
                log?.Invoke($"  ✗ rewrite failed: {r.LocalPath}");
            }
        }

        WriteServeScripts(outputDirectory);
        log?.Invoke($"  Paths rewritten: {htmlDone} HTML, {cssDone} CSS - {filesRoot}");
    }

    // ------------------------------------------------------------------- HTML

    private static readonly string[] HtmlAttrs = ["href", "src", "srcset", "data-src",
        "data-srcset", "action", "poster"];

    /// <summary>
    /// Skipped schemes: inline data, inline scripts, pure anchors and
    /// contact/email links have no archive counterpart.
    /// </summary>
    private static bool IsSkippable(string link) =>
        link.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
        link.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
        link.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
        link.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
        link.StartsWith('#');

    /// <summary>Rewrites the links of one HTML file. Returns the number of rewritten references.</summary>
    private static void RewriteHtml(string file, string pageUrl, string filesRoot,
        Dictionary<string, string> urlMap, Counter counter)
    {
        var doc = new HtmlParser().ParseDocument(File.ReadAllText(file));

        // a wrong/forged <base href> would break relative path resolution: drop it
        foreach (var b in doc.QuerySelectorAll("base")) b.Remove();

        var count = counter.Rewritten;

        foreach (var el in doc.QuerySelectorAll("*"))
        {
            foreach (var attr in HtmlAttrs)
            {
                var value = el.GetAttribute(attr);
                if (string.IsNullOrWhiteSpace(value)) continue;

                // srcset-style: "url descriptor,url2 descriptor2"
                var parts = value.Split(',');
                var rewritten = false;
                for (var i = 0; i < parts.Length; i++)
                {
                    var token = parts[i].Trim();
                    var sep = token.IndexOf(' ');
                    var link = sep > 0 ? token[..sep].Trim() : token;
                    var suffix = sep > 0 ? token[sep..] : "";

                    if (IsSkippable(link)) continue;

                    if (Resolve(link, pageUrl, file, filesRoot, urlMap) is { } target)
                    {
                        parts[i] = target + suffix;
                        rewritten = true;
                        counter.Rewritten++;
                    }
                }
                if (rewritten) el.SetAttribute(attr, string.Join(", ", parts));
            }

            // inline style="" attribute: background-image url(...) references
            var styleAttr = el.GetAttribute("style");
            if (!string.IsNullOrWhiteSpace(styleAttr) && styleAttr.Contains("url("))
            {
                var rewrittenStyle = RewriteCssText(styleAttr, pageUrl, file, filesRoot, urlMap, counter);
                if (rewrittenStyle != styleAttr) el.SetAttribute("style", rewrittenStyle);
            }
        }

        // a PWA manifest left root-absolute only produces CORS console noise
        // when browsing the archive via file://: drop it when there is no local copy
        foreach (var l in doc.QuerySelectorAll("link[href]").ToList())
        {
            var rel = l.GetAttribute("rel")?.ToLowerInvariant() ?? "";
            if (rel.Contains("manifest") && (l.GetAttribute("href") ?? "").StartsWith('/'))
                l.Remove();
        }

        // inline <style> blocks: @font-face / background urls root-absolute
        foreach (var st in doc.QuerySelectorAll("style").ToList())
        {
            var text = st.TextContent ?? "";
            if (text.Length == 0) continue;
            if (text.Contains("url(") || text.Contains("@import"))
                st.TextContent = RewriteCssText(text, pageUrl, file, filesRoot, urlMap, counter);
        }

        File.WriteAllText(file, doc.DocumentElement.OuterHtml, Encoding.UTF8);
    }

    // -------------------------------------------------------------------- CSS

    /// <summary>Rewrites url() and @import references inside a CSS file.</summary>
    private static void RewriteCss(string file, string cssUrl, string filesRoot,
        Dictionary<string, string> urlMap, Counter counter)
    {
        var text = File.ReadAllText(file);
        File.WriteAllText(file, RewriteCssText(text, cssUrl, file, filesRoot, urlMap, counter), Encoding.UTF8);
    }

    /// <summary>Rewrites url()/@import inside any CSS text (file content, inline &lt;style&gt;, style attribute).</summary>
    private static string RewriteCssText(string text, string ownerUrl, string ownerFile,
        string filesRoot, Dictionary<string, string> urlMap, Counter counter)
    {
        text = CssUrlRegex().Replace(text, m =>
        {
            var url = m.Groups[2].Value.Trim();
            if (url.Length == 0 || IsSkippable(url)) return m.Value;

            if (Resolve(url, ownerUrl, ownerFile, filesRoot, urlMap) is not { } target) return m.Value;
            counter.Rewritten++;
            return $"url('{target.Replace("\\", "/")}')";
        });

        // @import "file.css"; (the url(...) form is handled above)
        text = CssImportRegex().Replace(text, m =>
        {
            var url = m.Groups[2].Value.Trim();
            if (url.StartsWith("url(", StringComparison.OrdinalIgnoreCase)) return m.Value;

            if (Resolve(url, ownerUrl, ownerFile, filesRoot, urlMap) is not { } target) return m.Value;
            counter.Rewritten++;
            return $"@import '{target.Replace("\\", "/")}'";
        });

        return text;
    }

    // ------------------------------------------------------------------- core

    /// <summary>
    /// Maps a reference found inside a local file to the archive path of its
    /// target, expressed relatively from the owner file directory. Returns null
    /// when the target has no local copy (the reference stays untouched).
    /// </summary>
    private static string? Resolve(string link, string ownerUrl, string ownerFile,
        string filesRoot, Dictionary<string, string> urlMap)
    {
        var value = link.Trim();
        if (value.Length == 0) return null;

        // keep the fragment: anchors still work against the local page copy
        var hashIndex = value.IndexOf('#');
        var hash = hashIndex >= 0 ? value[hashIndex..] : "";
        if (hashIndex >= 0) value = value[..hashIndex];
        if (value.Length == 0) return null;

        // absolute-ize against the owner page/asset URL (handles root-absolute
        // and site-relative listings equally)
        Uri? abs = null;
        if (Uri.TryCreate(ownerUrl, UriKind.Absolute, out var baseUri) &&
            Uri.TryCreate(baseUri, value, out var resolved))
            abs = resolved;
        else if (Uri.TryCreate(value, UriKind.Absolute, out var direct))
            abs = direct;

        if (abs is null) return null;

        if (urlMap.TryGetValue(NormUrl(abs.ToString()), out var mapped))
            return Relative(ownerFile, filesRoot, mapped) + hash;

        // protocol-relative links (//host/...) may only exist with https
        if (abs.Scheme == "http" &&
            urlMap.TryGetValue(NormUrl(abs.ToString().Replace("http://", "https://")), out var httpsMapped) &&
            File.Exists(Path.Combine(filesRoot, httpsMapped)))
            return Relative(ownerFile, filesRoot, httpsMapped) + hash;

        // same-host fallback: the local tree mirrors the site, so host+path
        // maps even when the recorded URL differed (redirects, query drops)
        if (abs.Host.Contains('.'))
        {
            var candidate = abs.Host + Uri.UnescapeDataString(abs.AbsolutePath);
            var candidateFull = Path.Combine(filesRoot, candidate.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidateFull))
                return Relative(ownerFile, filesRoot, candidate) + hash;
        }

        return null;
    }

    /// <summary>Local relative path between an owner file and an archive entry.</summary>
    private static string Relative(string ownerFile, string filesRoot, string targetPath)
    {
        var ownerDir = Path.GetDirectoryName(ownerFile)!;
        var target = Path.Combine(filesRoot, targetPath.Replace('/', Path.DirectorySeparatorChar));
        var rel = Path.GetRelativePath(ownerDir, target).Replace('\\', '/');
        return rel.StartsWith('.') ? rel : "./" + rel;
    }

    /// <summary>
    /// Expected archive path of a page URL within files/ (host + site tree,
    /// index.html for directory-style URLs). Only used for page-to-page links:
    /// the raw copy of a page follows the same naming rule as the tree.
    /// </summary>
    private static string? PageCandidatePath(Uri pageUrl)
    {
        var host = pageUrl.Host;
        var path = Uri.UnescapeDataString(pageUrl.AbsolutePath);

        if (path.EndsWith('/') || path.Length == 0 || !path.Contains('.', StringComparison.Ordinal))
            return host + (path.EndsWith('/') ? path : path + "/") + "index.html";

        return host + path;
    }

    /// <summary>URL key: scheme+host+path+query, no fragment.</summary>
    private static string NormUrl(string url)
    {
        try { return new Uri(url).GetLeftPart(UriPartial.Query).ToString(); }
        catch { return url; }
    }

    /// <summary>
    /// Writes tiny helper scripts next to files/: browsing the archive from a
    /// local HTTP server avoids the file:// security origin limits of Chrome
    /// (cross-origin loads inside frames, web app manifests).
    /// </summary>
    private static void WriteServeScripts(string outputDirectory)
    {
        try
        {
            var cmd = Path.Combine(outputDirectory, "serve.cmd");
            var sh = Path.Combine(outputDirectory, "serve.sh");
            if (!File.Exists(cmd))
                File.WriteAllText(cmd,
                    "@echo off\r\n" +
                    "REM  Serves the files/ archive over HTTP (browsing it with file:// blocks\r\n" +
                    "REM  some cross-origin loads in Chrome). Stop with CTRL+C.\r\n" +
                    "python -m http.server 8080 --bind 127.0.0.1 --directory files\r\n" +
                    "if errorlevel 9009 (\r\n" +
                    "  echo Python not found. Install it or use any static server, e.g.:\r\n" +
                    "  echo   dotnet tool install -g dotnet-serve ^&^& dotnet-serve .\\files -p 8080\r\n" +
                    ")\r\n", Encoding.UTF8);
            if (!File.Exists(sh))
                File.WriteAllText(sh,
                    "#!/usr/bin/env sh\n" +
                    "# Serves the files/ archive over HTTP (browsing it with file:// blocks\n" +
                    "# some cross-origin loads in Chrome/WebKit).\n" +
                    "python3 -m http.server 8080 --bind 127.0.0.1 --directory files\n", Encoding.UTF8);
        }
        catch
        {
            // never a reason to fail the rewrite phase
        }
    }

    /// <summary>url(x) or url("x") inside CSS.</summary>
    [GeneratedRegex(@"(?i)url\(\s*(['""]?)([^)'"" ]+)\1\s*\)")]
    private static partial Regex CssUrlRegex();

    /// <summary>@import "file.css"; (the url() form is handled by CssUrlRegex).</summary>
    [GeneratedRegex(@"(?i)@import\s+(['""])([^'""]+)\1\s*;?")]
    private static partial Regex CssImportRegex();
}

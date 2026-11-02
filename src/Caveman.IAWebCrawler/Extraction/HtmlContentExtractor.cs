using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Caveman.IAWebCrawler;

namespace Caveman.IAWebCrawler.Extraction;

/// <summary>
/// Extracts every meaningful piece of content from an HTML page:
/// metadata, headings, paragraphs, links, images (any source, including
/// inline base64 data URIs), tables, forms and JSON-LD.
/// Also exposes a "resource discovery only" mode used by the Files mode.
/// </summary>
public sealed partial class HtmlContentExtractor
{
    private readonly HtmlParser _parser = new();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // url(...) inside inline styles and <image href> inside inline SVG
    [GeneratedRegex(@"url\(\s*['""]?(data:[^'""()]+|[^'""())]+)['""]?\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex InlineCssUrl();

    private static string Clean(string? s) =>
        WhitespaceRegex().Replace(s ?? "", " ").Trim();

    /// <summary>Parses HTML and returns the IDocument.</summary>
    public IDocument Parse(string html) => _parser.ParseDocument(html);

    /// <summary>
    /// Finds all resources referenced by a page (to queue downloads / visits).
    /// Also used by the Files-only mode, where metadata are not needed.
    /// Covers: anchors, img src/srcset/data-src/data-srcset (lazy CMS), scripts,
    /// every stylesheet link (rel=stylesheet AND rel=preload as=style used by
    /// high-priority CMS), icons, og:image / twitter:image metas, preload as=image,
    /// media elements, object/embed/iframe and inline svg &lt;image&gt;.
    /// Inline base64 data URIs are recognized as embedded images (no download).
    /// </summary>
    public List<ResourceTarget> GetResourceTargets(IDocument doc, Uri pageUrl)
    {
        var targets = new List<ResourceTarget>();

        void AddResource(string? href, ResourceType type, string? text)
        {
            if (string.IsNullOrWhiteSpace(href)) return;

            // inline base64 images: they are already inside the page, nothing to download
            if (href!.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                targets.Add(new ResourceTarget(new Uri(LimitDataUri(href)), ResourceType.Image,
                    text ?? "embedded", Embedded: true));
                return;
            }

            var t = href.Trim();
            if (t.StartsWith("mailto:") || t.StartsWith("tel:") ||
                t.StartsWith("javascript:") || t.StartsWith("data:") || t.StartsWith("#"))
                return;
            if (!Uri.TryCreate(pageUrl, t, out var abs)) return;
            if (abs.Scheme != Uri.UriSchemeHttp && abs.Scheme != Uri.UriSchemeHttps) return;
            targets.Add(new ResourceTarget(abs, type, Clean(text)));
        }

        foreach (var a in doc.QuerySelectorAll("a[href]"))
            AddResource(a.GetAttribute("href"), ResourceType.Page, a.TextContent);

        foreach (var img in doc.QuerySelectorAll("img[src]"))
            AddResource(img.GetAttribute("src"), ResourceType.Image, img.GetAttribute("alt"));

        foreach (var s in doc.QuerySelectorAll("script[src]"))
            AddResource(s.GetAttribute("src"), ResourceType.Script, "");

        // stylesheets: both classic rel=stylesheet and rel="preload" as="style"
        // (CMS pages switch rel at runtime; without this some sheets are missed)
        foreach (var l in doc.QuerySelectorAll("link[href]"))
        {
            var rel = l.GetAttribute("rel") ?? "";
            var asAttr = l.GetAttribute("as") ?? "";
            if (rel.Contains("stylesheet") ||
                (rel.Contains("preload") && asAttr.Equals("style", StringComparison.OrdinalIgnoreCase)))
                AddResource(l.GetAttribute("href"), ResourceType.Stylesheet, "");
            else if (rel.Contains("icon"))
                AddResource(l.GetAttribute("href"), ResourceType.Image, "favicon");
            // high-priority hero images declared with <link rel=preload as=image>
            else if (rel.Contains("preload") && asAttr.Equals("image", StringComparison.OrdinalIgnoreCase))
                AddResource(l.GetAttribute("href"), ResourceType.Image, "preload");
            // preload of fonts declared in HTML (extension classifies them in the queue)
            else if (rel.Contains("preload") && asAttr.Equals("font", StringComparison.OrdinalIgnoreCase))
                AddResource(l.GetAttribute("href"), ResourceType.Font, "");
            // PWA manifest: harmless in the archive but keeps the local copy complete
            else if (rel.Contains("manifest"))
                AddResource(l.GetAttribute("href"), ResourceType.Other, "manifest");
        }

        // inline <style> blocks: @font-face, background-image url() and @import —
        // the fonts/images referenced only here are invisible to the tag scan
        void ScanCssAttrib(string cssText)
        {
            if (string.IsNullOrWhiteSpace(cssText)) return;
            foreach (Match m in CssUrlRef().Matches(cssText))
                AddResource(m.Groups[1].Value, ResourceType.Other, "css-ref");
            foreach (Match m in CssImportRef().Matches(cssText)) // @import "x.css"
                AddResource(m.Groups[2].Value, ResourceType.Other, "css-ref");
        }
        foreach (var st in doc.QuerySelectorAll("style"))
            ScanCssAttrib(st.TextContent ?? "");

        // inline style="" attributes (background-image:url(...) carousels/hero)
        foreach (var el in doc.QuerySelectorAll("[style]"))
            ScanCssAttrib(el.GetAttribute("style") ?? "");

        foreach (var el in doc.QuerySelectorAll("source[src], iframe[src], embed[src], video[src], audio[src]"))
            AddResource(el.GetAttribute("src"), ResourceType.Media, "");

        // poster frames of videos
        foreach (var el in doc.QuerySelectorAll("video[poster]"))
            AddResource(el.GetAttribute("poster"), ResourceType.Image, "poster");

        // lazy-load CMS: <img data-src="..."> e <img data-srcset="...">
        foreach (var el in doc.QuerySelectorAll("[data-src]"))
            AddResource(el.GetAttribute("data-src"), ResourceType.Image, "");

        // srcset (responsive images): "url 1x, url2 2x" — both eager and lazy
        foreach (var el in doc.QuerySelectorAll("img[srcset], source[srcset], img[data-srcset], source[data-srcset]"))
            foreach (var attr in new[] { "srcset", "data-srcset" })
            {
                if (!el.HasAttribute(attr)) continue;
                foreach (var part in (el.GetAttribute(attr) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var tokens = part.Trim().Split(' ', 2);
                    if (tokens.Length > 0) AddResource(tokens[0], ResourceType.Image, "");
                }
            }

        foreach (var el in doc.QuerySelectorAll("object[data]"))
            AddResource(el.GetAttribute("data"), ResourceType.Other, "");

        // social preview images: og:image, twitter:image / twitter:image:src
        foreach (var m in doc.QuerySelectorAll("meta[property], meta[name]"))
        {
            var key = (m.GetAttribute("property") ?? m.GetAttribute("name") ?? "").ToLowerInvariant();
            if (key is "og:image" or "og:image:url" or "og:image:secure_url" or
                "twitter:image" or "twitter:image:src")
                AddResource(m.GetAttribute("content"), ResourceType.Image, key);
        }

        // inline svg images: <image href="..."> / <image xlink:href="...">
        foreach (var img in doc.QuerySelectorAll("svg image"))
        {
            var href = img.GetAttribute("href") ?? img.GetAttribute("xlink:href");
            AddResource(href, ResourceType.Image, "svg");
        }

        // svg sprite references: <use href="/sprites.svg#icon"> — without the
        // downloaded sheet every icon of the site disappears in the local copy
        foreach (var use in doc.QuerySelectorAll("use[href], svg [xlink\\:href]"))
        {
            var href = use.GetAttribute("href") ?? use.GetAttribute("xlink:href");
            AddResource(href, ResourceType.Other, "sprite");
        }

        // inline style background images: style="background-image: url(...)"
        foreach (var el in doc.QuerySelectorAll("[style]"))
            foreach (Match m in InlineCssUrl().Matches(el.GetAttribute("style") ?? ""))
                AddResource(m.Groups[1].Value, ResourceType.Image, "background");

        return targets;
    }

    /// <summary>Trims huge inline base64 payloads for metadata, keeping a fragment for reference.</summary>
    private static string LimitDataUri(string uri)
    {
        const int max = 160;
        return uri.Length <= max ? uri : uri[..max] + $"…[base64 importato, {uri.Length} caratteri]";
    }

    /// <summary>Complete extraction of an HTML page.</summary>
    public PageResult Extract(IDocument doc, Uri pageUrl, string discoveredFrom, int depth)
    {
        var page = new PageResult
        {
            Url = pageUrl.ToString(),
            DiscoveredFrom = discoveredFrom,
            Depth = depth,
            CrawledAtUtc = DateTime.UtcNow
        };

        // --- Base metadata ---
        page.Title = Clean(doc.Title);
        page.Language = doc.DocumentElement?.GetAttribute("lang");
        page.Canonical = doc.QuerySelector("link[rel='canonical']")?.GetAttribute("href");

        foreach (var m in doc.QuerySelectorAll("meta"))
        {
            var key = m.GetAttribute("name")
                      ?? m.GetAttribute("property")
                      ?? m.GetAttribute("http-equiv");
            var content = m.GetAttribute("content");
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(content))
                page.MetaTags[key] = content;
        }

        // --- JSON-LD (structured data) ---
        foreach (var s in doc.QuerySelectorAll("script[type='application/ld+json']"))
            if (!string.IsNullOrWhiteSpace(s.TextContent))
                page.JsonLdScripts.Add(s.TextContent.Trim());

        // --- Headings H1..H6 ---
        foreach (var h in doc.QuerySelectorAll("h1,h2,h3,h4,h5,h6"))
        {
            var t = Clean(h.TextContent);
            if (t.Length > 0)
                page.Headings.Add(new HeadingInfo { Level = h.TagName[1] - '0', Text = t });
        }

        // --- Paragraphs (outside nav/footer/aside) ---
        foreach (var p in doc.QuerySelectorAll("p"))
        {
            if (p.Closest("nav,footer,aside,script,style") is not null) continue;
            var t = Clean(p.TextContent);
            if (t.Length > 10) page.Paragraphs.Add(t);
        }

        // --- Tables ---
        foreach (var table in doc.QuerySelectorAll("table"))
        {
            var tab = new TableInfo();
            foreach (var tr in table.QuerySelectorAll("tr"))
            {
                var row = tr.QuerySelectorAll("th,td").Select(c => Clean(c.TextContent)).ToList();
                if (row.Count > 0) tab.Rows.Add(row);
            }
            if (tab.Rows.Count > 0) page.Tables.Add(tab);
        }

        // --- Forms ---
        foreach (var f in doc.QuerySelectorAll("form"))
        {
            var form = new FormInfo
            {
                Action = Resolve(doc.BaseUri ?? pageUrl.ToString(), f.GetAttribute("action")) ?? "",
                Method = (f.GetAttribute("method") ?? "get").ToUpperInvariant()
            };
            foreach (var el in f.QuerySelectorAll("input,select,textarea,button"))
                form.Fields.Add(new FormFieldInfo
                {
                    Name = el.GetAttribute("name") ?? "",
                    Type = el.GetAttribute("type") ?? el.TagName.ToLowerInvariant()
                });
            page.Forms.Add(form);
        }

        // --- Links, images, scripts, stylesheets ---
        foreach (var t in GetResourceTargets(doc, pageUrl))
        {
            switch (t.Type)
            {
                case ResourceType.Page:
                    page.Links.Add(new LinkInfo
                    {
                        Href = t.Uri.ToString(),
                        Text = t.Text,
                        Internal = false // the crawler updates the flag with the real host
                    });
                    break;
                case ResourceType.Image:
                    page.Images.Add(new ImageInfo
                    {
                        Src = t.Uri.ToString(),
                        Alt = t.Text,
                        Embedded = t.Embedded // data URI already inline in the page
                    });
                    break;
                case ResourceType.Script:
                    page.Scripts.Add(t.Uri.ToString());
                    break;
                case ResourceType.Stylesheet:
                    page.Stylesheets.Add(t.Uri.ToString());
                    break;
            }
        }

        // mailto/tel: the original extraction kept these as information
        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href") ?? "";
            if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
                page.Links.Add(new LinkInfo { Href = href, Text = Clean(a.TextContent), Internal = false });
        }

        page.Markdown = MarkdownRenderer.Render(doc, pageUrl);
        return page;
    }

    [GeneratedRegex(@"url\(\s*['""]?([^)'""]+)['""]?\s*\)")]
    private static partial Regex CssUrlRef();

    [GeneratedRegex(@"(?i)@import\s+(['""])([^'""]+)\1")]
    private static partial Regex CssImportRef();

    private static string Resolve(string baseline, string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return baseline;
        return Uri.TryCreate(new Uri(baseline), href, out var u) ? u.ToString() : baseline;    }
}

/// <summary>
/// A resource referenced by a page, to queue for download or visit.
/// <paramref name="Embedded"/> marks inline base64 payloads: information only,
/// nothing to fetch from the network.
/// </summary>
public sealed record ResourceTarget(Uri Uri, ResourceType Type, string Text, bool Embedded = false);

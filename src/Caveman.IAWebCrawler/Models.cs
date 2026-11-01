using System.Text.Json.Serialization;

namespace Caveman.IAWebCrawler;

/// <summary>Resource types found during a crawl.</summary>
public enum ResourceType
{
    Page,
    Document,
    Script,
    Stylesheet,
    Image,
    Media,
    Font,
    Other
}

/// <summary>Overall result of crawling a website.</summary>
public sealed class SiteCrawlResult
{
    public string StartUrl { get; set; } = "";
    public string Host { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }
    public CrawlModes Modes { get; set; }
    public CrawlStats Stats { get; set; } = new();
    public SiteInfo Site { get; set; } = new();
    public List<PageResult> Pages { get; set; } = new();
    public List<ResourceInfo> Resources { get; set; } = new();

    [JsonIgnore]
    public TimeSpan Duration => FinishedAtUtc - StartedAtUtc;
}

/// <summary>Aggregate statistics of a crawl.</summary>
public sealed class CrawlStats
{
    public int PagesCrawled { get; set; }
    public int FilesDownloaded { get; set; }
    public int Errors { get; set; }
    public int SkippedByRobots { get; set; }
    public long TotalBytes { get; set; }
    public List<string> ErrorDetails { get; set; } = new();
}

/// <summary>Site-level information (robots.txt, sitemaps).</summary>
public sealed class SiteInfo
{
    public string RobotsTxt { get; set; } = "";
    public List<string> SitemapUrls { get; set; } = new();
    public List<string> SitemapDiscovered { get; set; } = new();
}

/// <summary>Complete extraction of an HTML page.</summary>
public sealed class PageResult
{
    public string Url { get; set; } = "";
    public DateTime CrawledAtUtc { get; set; }
    public int? StatusCode { get; set; }
    public string ContentType { get; set; } = "";
    public long ContentLengthBytes { get; set; }
    public string? LastModified { get; set; }
    public int Depth { get; set; }
    public string? DiscoveredFrom { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// True when the page was fetched after the crawl to complete the Markdown
    /// snapshot (pages referenced by teasers but not reached by depth/page limits).
    /// </summary>
    public bool Imported { get; set; }

    // --- Metadata ---
    public string Title { get; set; } = "";
    public string? Language { get; set; }
    public string? Canonical { get; set; }

    /// <summary>All meta tags: name/property/http-equiv → content.</summary>
    public Dictionary<string, string> MetaTags { get; set; } = new();

    /// <summary>JSON-LD blocks (structured data) found in the page.</summary>
    public List<string> JsonLdScripts { get; set; } = new();

    // --- Content ---
    public List<HeadingInfo> Headings { get; set; } = new();
    public List<string> Paragraphs { get; set; } = new();
    public List<LinkInfo> Links { get; set; } = new();
    public List<ImageInfo> Images { get; set; } = new();
    public List<TableInfo> Tables { get; set; } = new();
    public List<FormInfo> Forms { get; set; } = new();
    public List<string> Scripts { get; set; } = new();
    public List<string> Stylesheets { get; set; } = new();

    /// <summary>Page converted to Markdown (available in Json/Markdown modes only).</summary>
    public string Markdown { get; set; } = "";
}

public sealed class HeadingInfo
{
    public int Level { get; set; }          // 1 = H1 ... 6 = H6
    public string Text { get; set; } = "";
}

public sealed class LinkInfo
{
    public string Href { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>True = same domain as the site being crawled.</summary>
    public bool Internal { get; set; }
}

public sealed class ImageInfo
{
    public string Src { get; set; } = "";
    public string Alt { get; set; } = "";
    /// <summary>True for inline base64 (data URI) images; nothing to download, already in the page.</summary>
    public bool Embedded { get; set; }
}

public sealed class TableInfo
{
    public List<List<string>> Rows { get; set; } = new();
}

public sealed class FormInfo
{
    public string Action { get; set; } = "";
    public string Method { get; set; } = "";
    public List<FormFieldInfo> Fields { get; set; } = new();
}

public sealed class FormFieldInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
}

/// <summary>A file downloaded from the site (asset, document or raw response copy).</summary>
public sealed class ResourceInfo
{
    public string Url { get; set; } = "";
    public ResourceType Type { get; set; }
    public int StatusCode { get; set; }
    public string ContentType { get; set; } = "";
    public long LengthBytes { get; set; }
    /// <summary>Path of the local copy relative to <c>files/</c> (or <c>assets/</c>) in the output.</summary>
    public string LocalPath { get; set; } = "";
    public string? Error { get; set; }
    public string? DiscoveredFrom { get; set; }

    /// <summary>
    /// Text extracted from a document (pdf/docx/csv/json) for the RAG corpus:
    /// populated when <see cref="CrawlerOptions.RagIncludeDocuments"/> is on and no
    /// raw copy on disk already provides it.
    /// </summary>
    [JsonIgnore]
    public string? ExtractedText { get; set; }
}

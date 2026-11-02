namespace Caveman.IAWebCrawler;

/// <summary>Crawler output modes (combinable with OR).</summary>
[Flags]
public enum CrawlModes
{
    None = 0,

    /// <summary>All extracted information saved as JSON.</summary>
    Json = 1,

    /// <summary>Content in AI-optimized Markdown (bodies + index + combined file).</summary>
    Markdown = 2,

    /// <summary>No metadata extraction: just collect every file of the site (js, css, img, pdf...).</summary>
    Files = 4,

    All = Json | Markdown | Files
}

/// <summary>Crawling options.</summary>
public sealed class CrawlerOptions
{
    /// <summary>Start URL of the scan.</summary>
    public string StartUrl { get; set; } = "";

    /// <summary>Output modes (combinable flags).</summary>
    public CrawlModes Modes { get; set; } = CrawlModes.All;

    /// <summary>Folder where the outputs are written.</summary>
    public string OutputDirectory { get; set; } = "output";

    /// <summary>Maximum number of HTML pages to visit.</summary>
    public int MaxPages { get; set; } = 200;

    /// <summary>
    /// Maximum link navigation depth (0 = start page only; -1 = unlimited).
    /// Default 5: five link levels is the minimum recommended depth for an
    /// indexable snapshot; the limit applies to pages only — assets and
    /// documents are downloaded at any depth anyway.
    /// </summary>
    public int MaxDepth { get; set; } = 5;

    /// <summary>
    /// CSS recursion: after downloading a stylesheet, extract url() and @import,
    /// resolve relative addresses against the CSS URL and queue fonts, sprites,
    /// background images and imported sheets (so the local page renders standalone).
    /// </summary>
    public bool FollowCssReferences { get; set; } = true;

    /// <summary>Include the site's documents (pdf, docx, csv, json...) with their extracted text in the RAG corpus.</summary>
    public bool RagIncludeDocuments { get; set; } = true;

    /// <summary>Include the site's images (jpg, jpeg, png...) as metadata and descriptions in the RAG corpus.</summary>
    public bool RagIncludeImages { get; set; } = true;

    /// <summary>Maximum characters extracted from a single document for the RAG corpus.</summary>
    public int MaxDocumentTextChars { get; set; } = 500_000;

    /// <summary>Pause between requests, as politeness toward the server.</summary>
    public int DelayMilliseconds { get; set; } = 500;

    /// <summary>Follow links on the start host only (no fallback: subdomains excluded).</summary>
    public bool SameHostOnly { get; set; } = true;

    /// <summary>If false, external hosts are still downloaded when they appear as resources (assets/files).</summary>
    public bool DownloadExternalResources { get; set; } = false;

    /// <summary>
    /// When true, assets (css/js/images/fonts) referenced by pages are downloaded even
    /// from external hosts (e.g. icon fonts and stylesheets on CDNs). Static pages of
    /// external sites are NOT followed: only what is needed to render the archive.
    /// </summary>
    public bool DownloadExternalAssets { get; set; } = true;

    /// <summary>Honor the site's robots.txt (Disallow and Crawl-delay).</summary>
    public bool RespectRobotsTxt { get; set; } = true;

    /// <summary>Download and archive css/js/images/fonts/media.</summary>
    public bool DownloadAssets { get; set; } = true;

    /// <summary>Download and archive linked documents: pdf, docx, csv, json, txt, zip...</summary>
    public bool DownloadDocuments { get; set; } = true;

    /// <summary>Save a raw copy of every HTTP response (HTML included) into the <c>files/</c> folder.</summary>
    public bool SaveRawResponse { get; set; } = true;

    /// <summary>
    /// In Markdown/Json mode (no <c>files/</c> archive) still save downloaded images
    /// into <c>assets/</c>, so the Markdown can reference local copies instead of
    /// the live site. ON by default: an LLM snapshot should not depend on the site.
    /// </summary>
    public bool SaveAssetsForMarkdown { get; set; } = true;

    /// <summary>Maximum size for a single downloaded file.</summary>
    public long MaxFileBytes { get; set; } = 60 * 1024 * 1024;

    /// <summary>Also generate a single Markdown file with the whole site (useful for IA/RAG pipelines).</summary>
    public bool CombinedMarkdownFile { get; set; } = true;

    /// <summary>Maximum number of sitemap.xml URLs (useful to discover unlinked pages).</summary>
    public int SitemapMaxUrls { get; set; } = 2000;

    /// <summary>User-Agent used by requests.</summary>
    public string UserAgent { get; set; } = "Caveman.IAWebCrawler/0.1";

    /// <summary>Timeout for a single HTTP request.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Browser profile used in the HTTP headers (User-Agent, client-hint):
    /// pick one from <see cref="Browsers"/> — 20 pre-built identities across
    /// Windows, macOS, Linux, Android and iOS. Default: Chrome on Windows.
    /// </summary>
    public BrowserKind BrowserIdentity { get; set; } = BrowserKind.ChromeWindows;

    /// <summary>Accept-Language header value (language declared by the simulated browser).</summary>
    public string AcceptLanguage { get; set; } = "it-IT,it;q=0.9,en;q=0.8";

    /// <summary>Initial cookies; key = cookie name, value = cookie value.</summary>
    public Dictionary<string, string> Cookies { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Extra HTTP headers for every request (session tokens, router/proxy headers,
    /// additional client-hints). Key = header name, value = value to send.
    /// </summary>
    public Dictionary<string, string> ExtraHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// «First visit» warm-up before the scan: some sites return session cookies,
    /// anti-bot tokens or consent cookies on the first request. They are stored
    /// and carried along all the following calls (pages, assets, documents).
    /// </summary>
    public bool SessionWarmUp { get; set; } = true;

    /// <summary>
    /// Complete the Markdown snapshot: any internal page referenced by a body link
    /// that the depth/page limits did not reach is fetched at the end (text only)
    /// and included in the output. Without it, teasers like "Read more..." point
    /// back to the live site. Cap: <see cref="MaxExpansionPages"/>.
    /// </summary>
    public bool ExpandUncrawledLinks { get; set; } = true;

    /// <summary>Maximum number of pages fetched for the Markdown snapshot expansion.</summary>
    public int MaxExpansionPages { get; set; } = 100;

    /// <summary>
    /// Internal page links left without a copy in the archive (page/depth limits)
    /// are rewritten to the live absolute URL instead of a dead file:/// path:
    /// clicking them still opens the real page. Sub-resources are not touched
    /// (they are downloaded, so they always stay local).
    /// </summary>
    public bool MissingInternalLinksToLiveUrl { get; set; } = true;

    /// <summary>
    /// Declarative scraping recipes (JSON recipe files or recipe folders). They run
    /// after the crawl on the pages matching their URL pattern, and the results are
    /// written into <c>scrape/</c> (one JSON file per recipe) and exposed via
    /// <see cref="CavemanWebCrawler.ScrapeResults"/> for LINQ consumers.
    /// </summary>
    public HashSet<string> ScrapeRecipes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional logging callback.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>De-minify (beautify) archived js/css so they stay readable.</summary>
    public bool BeautifyAssets { get; set; } = true;

    /// <summary>Generate the AI/LLM/RAG deliverables: llms.txt, llms-full.txt, RAG chunks.</summary>
    public bool GenerateAiOutputs { get; set; } = true;

    /// <summary>Target size of a RAG chunk, in characters.</summary>
    public int RagChunkSize { get; set; } = 900;

    /// <summary>Overlap between consecutive chunks, in characters.</summary>
    public int RagChunkOverlap { get; set; } = 150;

    /// <summary>Extensions treated as documents to download and archive.</summary>
    public HashSet<string> DocumentExtensions { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "doc", "docx", "dotx", "xls", "xlsx", "xlsm", "csv", "tsv",
        "json", "txt", "rtf", "odt", "ods", "odp", "ppt", "pptx",
        "zip", "rar", "7z", "xml", "ics", "kml"
    };

    /// <summary>Extensions treated as static assets (downloaded only when <see cref="DownloadAssets"/> is on).</summary>
    public HashSet<string> AssetExtensions { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "css", "js", "mjs", "map",
        "png", "jpg", "jpeg", "gif", "svg", "webp", "avif", "bmp", "ico",
        "woff", "woff2", "ttf", "otf", "eot",
        "mp4", "webm", "mp3", "wav", "ogg"
    };
}

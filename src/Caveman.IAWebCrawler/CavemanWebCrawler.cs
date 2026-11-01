using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Caveman.IAWebCrawler.Extraction;

namespace Caveman.IAWebCrawler;

    /// <summary>
    /// <para>
    /// QuartzSoft "Caveman" crawler: downloads a website and produces up to three
    /// outputs (combinable flags, see <see cref="CrawlerOptions.Modes"/>):
    /// </para>
    /// <list type="bullet">
    /// <item><b>Json</b> – all information extracted from the pages into one results.json.</item>
    /// <item><b>Markdown</b> – one .md page per URL (AI-friendly style), index.md and a combined file.</item>
    /// <item><b>Files</b> – raw archive in files/: html, js, css, images, fonts, pdf, docx, csv... no metadata extraction.</item>
    /// </list>
    /// </summary>
    public sealed partial class CavemanWebCrawler : IDisposable
    {
        private readonly CrawlerOptions _options;
        private readonly HttpClient _http;
        private readonly bool _disposeHttp;
        private Uri _siteRoot = new("http://localhost/");
        private string _host = "";

        private readonly HashSet<string> _visited = new(StringComparer.Ordinal);
        private readonly Queue<(string Url, int Depth, string From)> _queue = new();
        private readonly Dictionary<string, ResourceType> _resourceTypes = new(StringComparer.Ordinal);

        private readonly List<PageResult> _pages = new();
        private readonly List<ResourceInfo> _resources = new();
        private readonly CrawlStats _stats = new();
        private readonly SiteInfo _site = new();
        private RobotsEvaluator _robots = new();

        /// <summary>
        /// Session cookies captured from responses when the caller provided an
        /// HttpClient without cookie management. They are replayed on every
        /// subsequent request, so tokens set at the first visit are carried along.
        /// </summary>
        private readonly Dictionary<string, string> _manualCookies = new(StringComparer.OrdinalIgnoreCase);

        public CavemanWebCrawler(CrawlerOptions options) : this(options, httpClient: null) { }

        public CavemanWebCrawler(CrawlerOptions options, HttpClient? httpClient)
        {
            _options = options;
            if (!Uri.TryCreate(options.StartUrl, UriKind.Absolute, out var start) ||
                start.Scheme is not ("http" or "https"))
                throw new ArgumentException("StartUrl is not a valid http/https URL.", nameof(options));
            _siteRoot = new Uri(start.GetLeftPart(UriPartial.Authority));
            _host = start.Host;

            if (httpClient is null)
            {
                var handler = new SocketsHttpHandler
                {
                    AllowAutoRedirect = true,
                    MaxAutomaticRedirections = 5,
                    AutomaticDecompression = DecompressionMethods.All,
                    UseCookies = true,
                    CookieContainer = _cookieContainer = new CookieContainer()
                };
                _http = new HttpClient(handler) { Timeout = options.RequestTimeout };
                _disposeHttp = true;
            }
            else
            {
                _http = httpClient;
            }
            ApplyIdentity();
        }

        private readonly CookieContainer? _cookieContainer;

        /// <summary>
        /// Applies the requested identity to the request headers: browser profile
        /// (User-Agent + client-hints), language, initial cookies and extra headers.
        /// A real profile reduces the risk of being blocked by anti-bot systems.
        /// </summary>
        private void ApplyIdentity()
        {
            var h = _http.DefaultRequestHeaders;
            var profile = Browsers.Get(_options.BrowserIdentity);

            h.UserAgent.Clear();
            h.UserAgent.ParseAdd(profile?.Ua ?? _options.UserAgent);

            if (profile?.Accept is not null) h.TryAddWithoutValidation("Accept", profile.Accept);
            var lang = AcceptLanguages.HeaderFor(_options.AcceptLanguage) ?? _options.AcceptLanguage;
            if (!string.IsNullOrWhiteSpace(lang))
                h.TryAddWithoutValidation("Accept-Language", lang);
            if (profile is not null)
                foreach (var (name, value) in profile.Hints)
                    h.TryAddWithoutValidation(name, value);
            foreach (var (name, value) in _options.ExtraHeaders)
                h.TryAddWithoutValidation(name, value);

            // initial cookies: with our own handler they go into the CookieContainer
            // (which then manages any Set-Cookie coming from the site by itself)
            if (_options.Cookies.Count > 0)
            {
                if (_cookieContainer is not null)
                    foreach (var (name, value) in _options.Cookies)
                        try { _cookieContainer.Add(_siteRoot, new Cookie(name, value)); }
                        catch { /* malformed cookie shape: ignored */ }
                else
                    foreach (var (name, value) in _options.Cookies)
                        _manualCookies.TryAdd(name, value);
            }
        }

     /// <summary>Runs the crawl and writes the requested outputs. Returns the full result.</summary>
    public async Task<SiteCrawlResult> CrawlAsync(CancellationToken ct = default)
    {
        var result = new SiteCrawlResult
        {
            StartUrl = _options.StartUrl,
            Host = _host,
            StartedAtUtc = DateTime.UtcNow,
            Modes = _options.Modes
        };

        Directory.CreateDirectory(_options.OutputDirectory);
        Log($"Caveman.IAWebCrawler → {_options.StartUrl}");
        Log($"Modes: {_options.Modes} | Output: {Path.GetFullPath(_options.OutputDirectory)}");

         // --- first contact: establishes the site session (tokens/cookies) ---
         // many sites (municipal CMS included) release a session/consent cookie or
         // an anti-bot token on the first hit: it must be acquired BEFORE and
         // replayed on every subsequent call. The warm-up step does this.
        if (_options.SessionWarmUp)
            await WarmUpAsync(ct);

        // --- robots.txt ---
        if (_options.RespectRobotsTxt)
        {
            _robots = await RobotsEvaluator.FetchAsync(_http, _siteRoot, _options.UserAgent, ct);
            _site.RobotsTxt = _robots.Raw;
            if (_robots.CrawlDelayMilliseconds > _options.DelayMilliseconds)
                Log($"  robots.txt Crawl-delay = {_robots.CrawlDelayMilliseconds}ms");
        }

        // --- sitemap.xml ---
        if (_options.Modes != CrawlModes.Files)
            await TryFetchSitemapAsync(ct);

         // --- start ---
        Enqueue(_options.StartUrl, 0, origin: null, ResourceType.Page);

        var delay = Math.Max(_options.DelayMilliseconds, _robots.CrawlDelayMilliseconds);

        while (_queue.Count > 0 && !ct.IsCancellationRequested)
        {
            var (url, depth, from) = _queue.Dequeue();
            if (!_visited.Add(url)) continue;

            // the MaxPages limit counts pages only: assets/documents
            // still in the queue are downloaded anyway (flush at the end of the crawl)
            var kind = _resourceTypes.TryGetValue(url, out var t) ? t : ResourceType.Page;
            if (kind == ResourceType.Page && _pages.Count >= _options.MaxPages)
            {
                // keep it outside _visited so the markdown snapshot expansion
                // can still import it at the end of the crawl
                _visited.Remove(url);
                continue;
            }

            if (_options.RespectRobotsTxt && !_robots.IsAllowed(new Uri(url)))
            {
                _stats.SkippedByRobots++;
                Log($"  ⋄ robots.txt blocks: {url}");
                continue;
            }

            await CrawlOneAsync(url, depth, from, ct);
            if (delay > 0) await Task.Delay(delay, ct);
        }

        // --- snapshot expansion for markdown: pages linked but not reached ---
        if (_options.Modes.HasFlag(CrawlModes.Markdown) && _options.GenerateAiOutputs &&
            _options.ExpandUncrawledLinks)
            await ExpandUncrawledLinksAsync(delay, ct);

        // --- output ---
        await WriteOutputsAsync(ct);

        result.Pages = _pages;
        result.Resources = _resources;
        result.Stats = _stats;
        result.Site = _site;
        result.FinishedAtUtc = DateTime.UtcNow;
        Log($"Completed: {_stats.PagesCrawled} pages, {_stats.FilesDownloaded} files, " +
            $"{_stats.Errors} errors, {_stats.TotalBytes / 1024.0:F0} KB in {result.Duration.TotalSeconds:F0}s");
        return result;
    }

    /// <summary>
    /// Completes the Markdown «snapshot» of the site: any internal page referenced
    /// by a body link of a crawled page but not reached because of the depth/page
    /// limits (the classic "Read more..." teasers) is fetched at the end — text
    /// only, session cookies already alive — and added as an imported page, so the
    /// Markdown references stay local and the model gets a self-contained picture
    /// of the site. Provenance is declared in the front matter of each file.
    /// </summary>
    private async Task ExpandUncrawledLinksAsync(int delay, CancellationToken ct)
    {
        var pending = new List<(string Url, string From)>();

        foreach (var page in _pages)
        foreach (var link in page.Links)
        {
            if (!link.Internal) continue;
            if (!Uri.TryCreate(link.Href, UriKind.Absolute, out var lu)) continue;
            if (lu.Scheme is not ("http" or "https")) continue;

            var clean = new Uri(lu.GetLeftPart(UriPartial.Query)).ToString();
            if (_visited.Contains(clean)) continue;

            var ext = GetExtension(lu);
            if (_options.DocumentExtensions.Contains(ext) || _options.AssetExtensions.Contains(ext))
                continue; // documents/assets are already handled by the dedicated downloads

            if (_options.RespectRobotsTxt && !_robots.IsAllowed(lu)) continue;

            if (!pending.Any(p => p.Url == clean))
                pending.Add((clean, page.Url));
        }

        if (pending.Count == 0) return;
        if (pending.Count > _options.MaxExpansionPages)
        {
            Log($"  ⋄ snapshot expansion: {pending.Count} linked pages not reached, fetching {_options.MaxExpansionPages}");
            pending = pending.Take(_options.MaxExpansionPages).ToList();
        }
        else
            Log($"  ⋄ snapshot expansion: {pending.Count} linked pages to include in the markdown");

        int imported = 0;
        foreach (var (url, from) in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var resp = await SendAsync(url, ct);
                var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (!resp.IsSuccessStatusCode || !contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    _visited.Add(url); // not an html page: don't retry later
                    continue;
                }
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length > _options.MaxFileBytes) { _visited.Add(url); continue; }

                var pageUri = resp.RequestMessage?.RequestUri ?? new Uri(url);
                var html = Decode(bytes, contentType);
                var doc = _extractor.Parse(html);
                var expanded = _extractor.Extract(doc, pageUri, from, depth: 0);
                expanded.StatusCode = 200;
                expanded.ContentType = contentType;
                expanded.ContentLengthBytes = bytes.Length;
                expanded.Imported = true;
                _visited.Add(url);
                if (!_visited.Contains(pageUri.ToString())) _visited.Add(pageUri.ToString());
                _pages.Add(expanded);
                imported++;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                Log($"  ✗ expansion {url}: {ex.Message}");
                _visited.Add(url);
            }

            if (delay > 0) await Task.Delay(delay, ct);
        }

        _stats.PagesCrawled = _pages.Count;
        if (imported > 0)
            Log($"  ✓ snapshot: {imported} pages imported into the markdown with their source");
    }

    // ------------------------------------------------------------------ crawl

    /// <summary>
     /// "First contact": a GET on the start page before any other call.
     /// The server may reply with session cookies, anti-bot tokens or consent
     /// cookies: they are stored and automatically replayed on every following
     /// request (pages, assets, documents). If the first hit is rejected
     /// (403/429, typical of filters waiting for the cookie) the retry follows after a pause.
    /// </summary>
    private async Task WarmUpAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var resp = await SendAsync(_options.StartUrl, ct);
                var cookieDesc = CaptureCookies(resp, _siteRoot);
                if (resp.IsSuccessStatusCode)
                {
                    if (cookieDesc.Length > 0)
                        Log($"  ✓ first contact: session established ({cookieDesc})");
                    return;
                }
                if ((int)resp.StatusCode is 403 or 429 && attempt == 1)
                {
                    Log($"  ⋄ first contact rejected (HTTP {(int)resp.StatusCode}): " +
                        "retrying after a short pause (probably a token/cookie requirement)");
                    await Task.Delay(Math.Max(_options.DelayMilliseconds, 1500), ct);
                    continue;
                }
                Log($"  ⋄ first contact: HTTP {(int)resp.StatusCode} (continuing anyway)");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"  ⋄ warm-up failed: {ex.Message}");
                return;
            }
        }
    }

    /// <summary>
     /// GET request carrying the session cookies captured at the first contact.
     /// With our own handler the CookieContainer takes care of them (UseCookies); when
     /// the caller passes its own HttpClient without cookie management, the Set-Cookie
     /// values received are manually replayed as a Cookie header.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (_options.Cookies.Count > 0 && _manualCookies.Count == 0)
            foreach (var (name, value) in _options.Cookies)
                _manualCookies.TryAdd(name, value);
        if (_cookieContainer is null && _manualCookies.Count > 0)
            req.Headers.TryAddWithoutValidation("Cookie",
                string.Join("; ", _manualCookies.Select(c => $"{c.Key}={c.Value}")));
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>
     /// Stores the received cookies and returns a "name=..., name2=..." description.
     /// With our own handler the CookieContainer already does it; this code runs only
     /// when the HttpClient was provided externally.
    /// </summary>
    private string CaptureCookies(HttpResponseMessage resp, Uri host)
    {
        var names = new List<string>();

        if (_cookieContainer is not null)
        {
            foreach (Cookie c in _cookieContainer.GetCookies(host))
                names.Add(c.Name);
        }
        else if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var sc in setCookies)
            {
                var pair = sc.Split(';', 2)[0];
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var name = pair[..eq].Trim();
                var value = pair[(eq + 1)..].Trim();
                _manualCookies[name] = value;
                names.Add(name);
            }
        }

        return names.Count == 0 ? "" :
            string.Join(", ", names.Take(4)) + (names.Count > 4 ? ", …" : "") + " — replayed on every following request";
    }

    private async Task CrawlOneAsync(string url, int depth, string? from, CancellationToken ct)
    {
        try
        {
            bool isPage = _resourceTypes.TryGetValue(url, out var plannedKind) &&
                          plannedKind == ResourceType.Page;
            if (isPage)
                Log($"[{_pages.Count + 1}/{_options.MaxPages}] D{depth} {url}");
            else
                Log($"  · asset D{depth} {url}");
            using var resp = await SendAsync(url, ct);
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            var finalUrl = resp.RequestMessage?.RequestUri ?? new Uri(url);

            if (!resp.IsSuccessStatusCode)
            {
                RecordError(url, $"HTTP {(int)resp.StatusCode}", from);
                return;
            }

            var ext = GetExtension(finalUrl);
            bool isCss = contentType.Contains("css", StringComparison.OrdinalIgnoreCase) || ext == "css";
            bool isHtml = contentType.Contains("html", StringComparison.OrdinalIgnoreCase);

            // already knows it is not HTML and no raw copy is needed? then no body:
            // type/size are recorded from the headers alone (fast, zero bytes).
            // EXCEPTIONS: CSS (for url()/@import recursion), documents (for the RAG
            // corpus text) and images in Markdown mode (local copies for the
            // self-contained snapshot) need the body downloaded anyway.
            bool wantDocText = !isHtml && !_options.SaveRawResponse &&
                               _options.GenerateAiOutputs &&
                               _options.Modes.HasFlag(CrawlModes.Markdown) &&
                               _options.RagIncludeDocuments &&
                               ClassifyUrl(finalUrl) == ResourceType.Document;
            bool wantImageCopy = !isHtml &&
                                 _options.SaveAssetsForMarkdown &&
                                 !_options.SaveRawResponse &&
                                 _options.Modes.HasFlag(CrawlModes.Markdown) &&
                                 IsInternal(finalUrl) &&
                                 ClassifyUrl(finalUrl) == ResourceType.Image;
            if (!isHtml && !isCss && !wantDocText && !wantImageCopy && !_options.SaveRawResponse)
            {
                RegisterResourceInfo(new ResourceInfo
                {
                    Url = finalUrl.ToString(),
                    Type = ClassifyUrl(finalUrl),
                    StatusCode = (int)resp.StatusCode,
                    ContentType = contentType,
                    LengthBytes = resp.Content.Headers.ContentLength ?? 0,
                    DiscoveredFrom = from
                });
                return;
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > _options.MaxFileBytes)
            {
                RecordError(url, $"File troppo grande ({bytes.Length} byte)", from);
                return;
            }

            isHtml = isHtml ||
                     (contentType.Length == 0 && LooksLikeHtml(bytes));
            var type = isHtml ? ResourceType.Page : ClassifyUrl(finalUrl);

             // raw copy into the files/ folder (Files mode, or whenever SaveRawResponse is on)
            if (_options.SaveRawResponse)
                SaveResponseFile(finalUrl, bytes, contentType, isHtml, depth, from, type);

            if (isHtml)
            {
                _visited.Add(finalUrl.ToString()); // avoid a second pass after a redirect
                CrawlHtmlAsync(bytes, finalUrl, url, depth, from, contentType);
            }
            else
            {
                if (!_options.SaveRawResponse)
                {
                    string? localPath = null;

                    // markdown mode: keep a local copy of the images in assets/, so
                    // the snapshot references the archive instead of the live site
                    if (wantImageCopy)
                    {
                        try
                        {
                            var local = LocalFilePathFor(finalUrl, isHtml: false, contentType);
                            var full = Path.Combine(_options.OutputDirectory, "assets", local);
                            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                            File.WriteAllBytes(full, bytes);
                            localPath = local;
                        }
                        catch (Exception ex)
                        {
                            Log($"  ✗ asset copy {url}: {ex.Message}");
                        }
                    }

                    // no raw copy: register the resource information
                    var info = RecordResource(finalUrl, bytes, contentType, (int)resp.StatusCode,
                        from, type, localPath);

                    // document text for the RAG corpus (pdf/docx/csv/json...)
                    if (wantDocText && _options.GenerateAiOutputs)
                    {
                        try
                        {
                            info.ExtractedText = await Extraction.DocumentTextExtractor
                                .ExtractAsync(bytes, ext, contentType, _options.MaxDocumentTextChars, ct);
                        }
                        catch (Exception ex)
                        {
                            Log($"  ✗ text extraction failed {url}: {ex.Message}");
                        }
                    }
                }

                 // --- CSS recursion: fonts, sprites, background images and @import sheets
                 // referenced by this stylesheet get queued as assets
                if (isCss && _options.FollowCssReferences)
                    EnqueueCssReferences(Decode(bytes, contentType), finalUrl, depth + 1, from ?? url);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or IOException)
        {
            RecordError(url, ex.Message, from);
        }
    }

    private void CrawlHtmlAsync(byte[] bytes, Uri pageUrl, string requestedUrl, int depth,
        string? from, string contentType)
    {
        var html = Decode(bytes, contentType);
        var doc = _extractor.Parse(html);
        var targets = _extractor.GetResourceTargets(doc, pageUrl);

        bool fullExtraction =
            _options.Modes.HasFlag(CrawlModes.Json) || _options.Modes.HasFlag(CrawlModes.Markdown);

        var page = fullExtraction
            ? _extractor.Extract(doc, pageUrl, from ?? requestedUrl, depth)
            : new PageResult
            {
                Url = pageUrl.ToString(),
                Depth = depth,
                DiscoveredFrom = from,
                CrawledAtUtc = DateTime.UtcNow
            };

        page.StatusCode = 200;
        page.ContentType = contentType;
        page.ContentLengthBytes = bytes.Length;
        if (fullExtraction)
        {
            foreach (var link in page.Links)
                if (Uri.TryCreate(link.Href, UriKind.Absolute, out var lu))
                    link.Internal = IsInternal(lu);
        }

        _pages.Add(page);
        _stats.PagesCrawled = _pages.Count;

         // --- queueing by depth ---
        var nextDepth = depth + 1;
        foreach (var t in targets)
        {
             // the depth limit applies to page navigation only:
             // assets and documents are downloaded at any depth anyway,
             // so distant links (e.g. pages at depth 10) bring their
             // pdf/images/fonts into the archive with the right depth
            if (t.Type == ResourceType.Page &&
                _options.MaxDepth >= 0 && nextDepth > _options.MaxDepth)
                continue;
            Enqueue(t.Uri.ToString(), nextDepth, pageUrl.ToString(), t.Type);
        }
    }

    private void Enqueue(string url, int depth, string? origin, ResourceType suggestedType)
    {
        if (_pages.Count >= _options.MaxPages && suggestedType == ResourceType.Page) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https")) return;

         // strips the fragment
        uri = new Uri(uri.GetLeftPart(UriPartial.Query));
        var clean = uri.ToString();
        if (clean.Length == 0 || _visited.Contains(clean)) return;

        bool internalUrl = IsInternal(uri);
        var ext = GetExtension(uri);

        if (_options.DocumentExtensions.Contains(ext))
        {
             // document: pdf/docx/csv/json/txt/zip...
            if (!_options.DownloadDocuments) return;
            if (!internalUrl && !_options.DownloadExternalResources) return;
            _resourceTypes[clean] = ResourceType.Document;
            _queue.Enqueue((clean, depth, origin ?? ""));
            return;
        }
        if (_options.AssetExtensions.Contains(ext))
        {
            // asset: css/js/images/fonts/media.
            // External CDNs (icon fonts, shared js/css) are downloaded when they
            // are needed to render the local copy (DownloadExternalAssets).
            if (!_options.DownloadAssets) return;
            if (!internalUrl && !_options.DownloadExternalResources && !_options.DownloadExternalAssets) return;
            _resourceTypes[clean] = ClassifyByExt(ext);
            _queue.Enqueue((clean, depth, origin ?? ""));
            return;
        }
        if (suggestedType != ResourceType.Page)
        {
             // explicit media/other (iframe, object, source...)
            if (!internalUrl && !_options.DownloadExternalResources) return;
            _resourceTypes[clean] = suggestedType;
            _queue.Enqueue((clean, depth, origin ?? ""));
            return;
        }

         // HTML page (internal only)
        if (!internalUrl) return;
        _resourceTypes[clean] = ResourceType.Page;
        _queue.Enqueue((clean, depth, origin ?? ""));
    }

    private bool IsInternal(Uri uri) =>
        string.Equals(uri.Host, _host, StringComparison.OrdinalIgnoreCase) &&
        uri.Scheme is "http" or "https";

    // ------------------------------------------------------------- download

    private readonly HtmlContentExtractor _extractor = new();

    private async Task TryFetchSitemapAsync(CancellationToken ct)
    {
        var sitemapUrl = _robots.Sitemaps.FirstOrDefault() ?? new Uri(_siteRoot, "/sitemap.xml").ToString();
        try
        {
            using var resp = await _http.GetAsync(sitemapUrl, ct);
            if (!resp.IsSuccessStatusCode) return;
            var xml = await resp.Content.ReadAsStringAsync(ct);
            if (!xml.Contains("<loc>", StringComparison.OrdinalIgnoreCase)) return;

            var locs = System.Text.RegularExpressions.Regex.Matches(xml, "<loc>\\s*(.*?)\\s*</loc>");
            foreach (System.Text.RegularExpressions.Match m in locs)
            {
                var loc = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
                if (loc.Length == 0) continue;
                _site.SitemapUrls.Add(loc);
                if (Uri.TryCreate(loc, UriKind.Absolute, out var u) && IsInternal(u) &&
                    _site.SitemapDiscovered.Count < _options.SitemapMaxUrls)
                {
                     // skip nested sitemaps (sitemap index)
                    if (!loc.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    {
                        _site.SitemapDiscovered.Add(loc);
                        Enqueue(loc, 0, origin: null, ResourceType.Page);
                    }
                }
            }
            Log($"  Sitemap: {_site.SitemapDiscovered.Count} URLs added from {sitemapUrl}");
        }
        catch { /* sitemap non disponibile: si continua con i soli link */ }
    }

    private static bool LooksLikeHtml(byte[] bytes)
    {
        var head = System.Text.Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 512));
        return head.TrimStart().StartsWith('<') &&
               (head.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
                head.Contains("<!doctype", StringComparison.OrdinalIgnoreCase));
    }

    private static string Decode(byte[] bytes, string contentType)
    {
        foreach (var part in contentType.Split(';'))
        {
            var p = part.Trim();
            if (p.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))
            {
                try { return System.Text.Encoding.GetEncoding(p["charset=".Length..].Trim()).GetString(bytes); }
                catch { break; }
            }
        }
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private ResourceType ClassifyUrl(Uri uri)
    {
        var ext = GetExtension(uri);
        if (ext.Length == 0) return ResourceType.Page;
        if (_options.DocumentExtensions.Contains(ext)) return ResourceType.Document;
        return ClassifyByExt(ext);
    }

    private static ResourceType ClassifyByExt(string ext) => ext.ToLowerInvariant() switch
    {
        "css" => ResourceType.Stylesheet,
        "js" or "mjs" or "map" => ResourceType.Script,
        "woff" or "woff2" or "ttf" or "otf" or "eot" => ResourceType.Font,
        "png" or "jpg" or "jpeg" or "gif" or "svg" or "webp" or "avif" or "bmp" or "ico" => ResourceType.Image,
        "mp4" or "webm" or "mp3" or "wav" or "ogg" => ResourceType.Media,
        _ => ResourceType.Other
    };

    private static string GetExtension(Uri uri)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        int slash = path.LastIndexOf('/');
        int dot = path.LastIndexOf('.');
        return dot > slash ? path[(dot + 1)..].ToLowerInvariant() : "";
    }

     // --------------------------------------------------------------- CSS recursion

    /// <summary>
     /// CSS recursion: stylesheets are treated like JS and their internal references
     /// are not lost. Every downloaded CSS is parsed for its url() and @import;
     /// relative addresses are resolved AGAINST the stylesheet URL and queued
     /// as assets (fonts, sprites, background images, imported stylesheets) - without this
     /// step the local page does not render (missing fonts/backgrounds).
    /// </summary>
    private void EnqueueCssReferences(string css, Uri cssUrl, int depth, string? from)
    {
        if (css.Length == 0) return;
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in CssUrlRegex().Matches(css))
            TryEnqueueCssRef(m.Groups["u"].Value, cssUrl, found, depth, from);

         // @import 'file.css'; - the url(...) form is already covered by CssUrlRegex
        foreach (Match m in CssImportRegex().Matches(css))
            TryEnqueueCssRef(m.Groups["u"].Value, cssUrl, found, depth, from);
    }

    private void TryEnqueueCssRef(string raw, Uri cssUrl, HashSet<string> found, int depth, string? from)
    {
        var u = raw.Trim().Trim('"', '\'');
        if (u.Length == 0 || u.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            u.StartsWith('#')) return;

        if (!Uri.TryCreate(cssUrl, u, out var abs) || abs.Scheme is not ("http" or "https"))
            return;
        if (!found.Add(abs.ToString())) return;

        var ext = GetExtension(abs);
        var type = _options.DocumentExtensions.Contains(ext)
            ? ResourceType.Document
            : ClassifyByExt(ext);
        Enqueue(abs.ToString(), depth, from, type);
    }

    [GeneratedRegex(@"url\(\s*(['""]?)(?<u>[^'""()]+)\1\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrlRegex();

    [GeneratedRegex(@"@import\s+(['""])(?<u>[^'"";]+)\1", RegexOptions.IgnoreCase)]
    private static partial Regex CssImportRegex();

    private ResourceInfo RecordResource(Uri url, byte[] bytes, string contentType, int status,
        string? from, ResourceType type, string? localPath)
    {
        var info = new ResourceInfo
        {
            Url = url.ToString(),
            Type = type,
            StatusCode = status,
            ContentType = contentType,
            LengthBytes = bytes.Length,
            LocalPath = localPath ?? "",
            DiscoveredFrom = from
        };
        RegisterResourceInfo(info);
        return info;
    }

     /// <summary>Registers the resource and updates the stats (FilesDownloaded and total bytes).</summary>
    private void RegisterResourceInfo(ResourceInfo info)
    {
        _resources.Add(info);
        _stats.FilesDownloaded++;
        _stats.TotalBytes += info.LengthBytes;
    }

    private void SaveResponseFile(Uri url, byte[] bytes, string contentType, bool isHtml,
        int depth, string? from, ResourceType type)
    {
        try
        {
            var local = LocalFilePathFor(url, isHtml, contentType);
            var full = Path.Combine(_options.OutputDirectory, "files", local);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            RecordResource(url, bytes, contentType, 200, from, type, local); // relative to the output archive
        }
        catch (Exception ex)
        {
            Log($"  ✗ unable to save {url}: {ex.Message}");
        }
    }

     /// <summary>Maps the URL to a local path under the output archive, following the site tree.</summary>
    private string LocalFilePathFor(Uri url, bool isHtml, string contentType)
    {
        var host = url.Host;
        var path = Uri.UnescapeDataString(url.AbsolutePath);
        var ext = GetExtension(url);

         // query: short suffix to avoid collisions
        string querySuffix = "";
        if (url.Query.Length > 0)
        {
            var h = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url.Query));
            querySuffix = "_" + Convert.ToHexString(h, 0, 4).ToLowerInvariant();
        }

        if (isHtml)
        {
            if (path.EndsWith('/')) path += "index.html" + querySuffix;
            else if (ext.Length == 0) path += "/index.html" + querySuffix;
            else path += querySuffix;
        }
        else
        {
            if (path.EndsWith('/') || ext.Length == 0)
            {
                path += querySuffix.Length > 0 ? "file" + querySuffix : "file";
                 // add the extension deduced from the content-type
                path += contentType switch
                {
                    "application/json" => ".json",
                    "text/plain" => ".txt",
                    "application/xml" or "text/xml" => ".xml",
                    _ => ""
                };
            }
            else path += querySuffix;
        }

         // sanitize the segments (Windows-safe, MAX_PATH-proof: segments capped at 80 chars)
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => SanitizeSegment(p))
            .ToList();
        if (parts.Count == 0) parts.Add("index.html");

        var rel = Path.Combine(host, Path.Combine(parts.ToArray()));

         // full path: when it exceeds the practical Windows limit (260),
         // the name is truncated keeping the extension and adding a hash
        var prefixLength = Path.GetFullPath(_options.OutputDirectory).Length + 1 /*files*/ + 1 /*slash*/;
        var maxTotal = 240;
        var over = prefixLength + rel.Length - maxTotal;
        if (over > 0)
        {
            var last = parts[^1];
            var stem = Path.GetFileNameWithoutExtension(last);
            var fileExt = Path.GetExtension(last);
            var maxStem = stem.Length - over - 9;     // "-hash8" takes 9 chars
            if (maxStem < 8) maxStem = 8;
            var h = ShortHash(url.ToString());
            parts[^1] = stem.Length > maxStem ? stem[..maxStem] + "-" + h + fileExt : last;
            rel = Path.Combine(host, Path.Combine(parts.ToArray()));

             // when even the segments alone are too many, the longest directories get shortened
            for (int i = parts.Count - 2; i >= 1 && prefixLength + rel.Length > maxTotal && i > -1; i--)
            {
                if (parts[i].Length > 30)
                {
                    parts[i] = parts[i][..20] + "-" + ShortHash(parts[i]);
                    rel = Path.Combine(host, Path.Combine(parts.ToArray()));
                }
            }
        }

        return rel;
    }

    private static string SanitizeSegment(string p)
    {
        p = p.Replace("..", "_").Replace(':', '_').TrimEnd();
        if (p.Length <= 80) return p;
         // very long names (e.g. CMS texture CACHE): truncate + hash to stay under MAX_PATH
        return p[..60] + "-" + ShortHash(p);
    }

    private static string ShortHash(string s)
    {
        var h = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(h, 0, 4).ToLowerInvariant();
    }

    private void RecordError(string url, string message, string? from)
    {
        _stats.Errors++;
        _stats.ErrorDetails.Add($"{url}: {message}");
        _resources.Add(new ResourceInfo { Url = url, Error = message, DiscoveredFrom = from, StatusCode = 0 });
        Log($"  ✗ {url}: {message}");
    }

    private void Log(string message) => _options.Log?.Invoke(message);

    // ---------------------------------------------------------------- output

    private async Task WriteOutputsAsync(CancellationToken ct)
    {
        var result = new SiteCrawlResult
        {
            StartUrl = _options.StartUrl,
            Host = _host,
            StartedAtUtc = _pages.Count > 0 ? _pages.Min(p => p.CrawledAtUtc) : DateTime.UtcNow,
            FinishedAtUtc = DateTime.UtcNow,
            Modes = _options.Modes,
            Stats = _stats,
            Site = _site,
            Pages = _pages,
            Resources = _resources
        };

        var modes = _options.Modes;

        if (modes.HasFlag(CrawlModes.Files)) Log($"  Files archived in: {Path.Combine(_options.OutputDirectory, "files")}");

        // riscrittura dei percorsi (href/src/url()) verso i file salvati + beautify js/css
        if (_options.SaveRawResponse)
        {
            Output.LinkRewriter.Rewrite(_pages, _resources, _options.OutputDirectory, Log);
            if (_options.BeautifyAssets)
                Output.AssetBeautifier.Beautify(_resources, _options.OutputDirectory, Log);
        }

        if (modes.HasFlag(CrawlModes.Json)) await Output.JsonOutputWriter.WriteAsync(result, _options.OutputDirectory, ct);
        if (modes.HasFlag(CrawlModes.Markdown))
        {
            await Output.MarkdownOutputWriter.WriteAsync(result, _options.OutputDirectory, _options.CombinedMarkdownFile, ct);
            if (_options.GenerateAiOutputs)
            {
                await Output.LlmsTxtWriter.WriteAsync(result, _options.OutputDirectory, ct);
                await Output.RagOutputWriter.WriteAsync(result, _options, ct);
            }
        }
    }

    public void Dispose()
    {
        if (_disposeHttp) _http.Dispose();
    }
}

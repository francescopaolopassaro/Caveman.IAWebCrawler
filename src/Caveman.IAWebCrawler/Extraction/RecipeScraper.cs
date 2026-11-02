using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Caveman.IAWebCrawler.Extraction;

/// <summary>
/// Applies the declarative recipes to the crawled pages: for every page matching
/// the recipe URL pattern, the CSS selectors extract the configured fields and
/// produce LINQ-friendly result objects. Pagination ("next page" selector) is
/// followed through the optional fetch delegate (the crawler provides one that
/// reuses its session, cookies and politeness delay).
/// </summary>
public sealed class RecipeScraper
{
    private readonly List<ScrapeRecipe> _recipes;
    private readonly Func<string, CancellationToken, Task<string?>>? _fetchHtml;
    private readonly Func<string, bool>? _urlAllowed;
    private readonly Action<string>? _log;

    public RecipeScraper(IEnumerable<ScrapeRecipe> recipes,
        Func<string, CancellationToken, Task<string?>>? fetchHtml = null,
        Func<string, bool>? urlAllowed = null,
        Action<string>? log = null)
    {
        // recipes whose load failed ("[load error...]") are skipped
        _recipes = recipes.Where(r => !r.Name.StartsWith('[')).ToList();
        _fetchHtml = fetchHtml;
        _urlAllowed = urlAllowed;
        _log = log;
    }

    /// <summary>Runs every recipe. Returns one result per recipe (same input order).</summary>
    public async Task<List<RecipeResult>> RunAsync(IEnumerable<PageResult> pages,
        CancellationToken ct = default)
    {
        var results = new List<RecipeResult>();
        var pageList = pages.Where(p => p.Error is null).ToList();

        foreach (var recipe in _recipes)
        {
            ct.ThrowIfCancellationRequested();
            var res = new RecipeResult { Recipe = recipe.Name };
            var htmlCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // worklist: matching crawled pages first, then the pagination chain
            var work = new Queue<string>(
                pageList.Where(p => RecipeMatches(recipe, p.Url)).Select(p => p.Url));

            while (work.Count > 0 && !ct.IsCancellationRequested && res.PaginationUrls.Count <= recipe.MaxNextPages)
            {
                var url = work.Dequeue();
                if (!done.Add(url)) continue;

                var cachedHtml = pageList.FirstOrDefault(p => OrdinalUrlEquals(p.Url, url))?.RawHtml;
                var html = await GetHtmlAsync(url, cachedHtml, htmlCache, ct);
                if (html is null) continue;

                res.PagesScanned.Add(url);
                var doc = new HtmlParser().ParseDocument(html);
                ScrapePage(recipe, url, doc, res);

                // pagination: enqueue the next page of the listing
                var nextUrl = NextPageUrl(recipe, url, doc, res);
                if (nextUrl is not null) work.Enqueue(nextUrl);
            }

            results.Add(res);
            _log?.Invoke($"  · recipe {recipe.Name}: {res.Items.Count} items from {res.PagesScanned.Count} page(s)" +
                         (res.PaginationUrls.Count > 0 ? $", pagination: {res.PaginationUrls.Count}" : ""));
        }

        return results;
    }

    // ------------------------------------------------------------------ core

    private bool RecipeMatches(ScrapeRecipe recipe, string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(recipe.UrlPattern)) return true;
        try { return Regex.IsMatch(pageUrl, recipe.UrlPattern); }
        catch (ArgumentException) { return pageUrl.Contains(recipe.UrlPattern, StringComparison.OrdinalIgnoreCase); }
    }

    private static bool OrdinalUrlEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void ScrapePage(ScrapeRecipe recipe, string pageUrl, IDocument doc, RecipeResult res)
    {
        // page-level fields merged into every item of this page
        var pageFields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in recipe.PageFields)
            pageFields[f.Name] = ExtractSingleValue(doc.DocumentElement, f);

        if (string.IsNullOrWhiteSpace(recipe.ItemSelector))
        {
            // one item per page (fields taken from the document root)
            var item = NewItem(pageUrl, pageFields);
            foreach (var f in recipe.Fields)
                ApplySingle(doc.DocumentElement, f, item);
            res.Items.Add(item);
        }
        else
        {
            foreach (var el in doc.QuerySelectorAll(recipe.ItemSelector))
            {
                var item = NewItem(pageUrl, pageFields);
                foreach (var f in recipe.Fields)
                    ApplySingle(el, f, item);
                res.Items.Add(item);
            }
        }
    }

    private string? NextPageUrl(ScrapeRecipe recipe, string pageUrl, IDocument doc, RecipeResult res)
    {
        if (string.IsNullOrWhiteSpace(recipe.NextPageSelector) ||
            res.PaginationUrls.Count >= recipe.MaxNextPages)
            return null;

        var href = doc.QuerySelector(recipe.NextPageSelector)?.GetAttribute("href");
        if (string.IsNullOrWhiteSpace(href)) return null;

        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri) ||
            !Uri.TryCreate(baseUri, href, out var nextPage))
            return null;

        if (nextPage.Scheme is not ("http" or "https")) return null;
        if (_urlAllowed is not null && !_urlAllowed(nextPage.ToString())) return null;

        var clean = new Uri(nextPage.GetLeftPart(UriPartial.Query)).ToString();
        if (res.PaginationUrls.Contains(clean)) return null;
        res.PaginationUrls.Add(clean);
        return clean;
    }

    private static ScrapedItem NewItem(string sourceUrl, Dictionary<string, string?> pageFields) =>
        new()
        {
            SourceUrl = sourceUrl,
            Fields = new Dictionary<string, string?>(pageFields, StringComparer.OrdinalIgnoreCase)
        };

    private void ApplySingle(IElement root, ScrapeField field, ScrapedItem item)
    {
        if (field.Name.Length == 0) return;

        var values = ExtractValues(root, field);

        if (field.MultipleSelector is not null)
        {
            item.Lists[field.Name] = values;
            item.Fields[field.Name] = string.Join(", ", values);
        }
        else
        {
            item.Fields[field.Name] = values.Count > 0 ? values[0] : field.Default;
        }
    }

    private string? ExtractSingleValue(IElement root, ScrapeField field)
    {
        var values = ExtractValues(root, field);
        return values.Count > 0 ? values[0] : field.Default;
    }

    private List<string> ExtractValues(IElement root, ScrapeField field)
    {
        IReadOnlyList<IElement> elements;

        if (field.MultipleSelector is not null)
            elements = root.QuerySelectorAll(field.MultipleSelector).ToArray();
        else if (!string.IsNullOrWhiteSpace(field.Selector))
        {
            var one = root.QuerySelector(field.Selector);
            if (one is null) return [];
            elements = [one];
        }
        else
        {
            elements = [root];
        }

        var result = new List<string>();
        foreach (var el in elements)
        {
            var attr = field.NormalizedAttribute;
            var value = attr switch
            {
                "text" or "" or "textcontent" => el.TextContent?.Trim(),
                "innerhtml" or "html" => el.InnerHtml?.Trim(),
                "outerhtml" => el.OuterHtml?.Trim(),
                _ => el.GetAttribute(attr) ?? el.Attributes
                        .FirstOrDefault(a => a.LocalName == attr)?.Value
            };
            if (string.IsNullOrWhiteSpace(value)) continue;

            if (field.Regex is not null)
            {
                var m = Regex.Match(value, field.Regex);
                if (!m.Success) continue;   // the regex also acts as a value filter
                value = m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
            }

            result.Add(value);
        }
        return result;
    }

    // --------------------------------------------------------------- fetching

    private async Task<string?> GetHtmlAsync(string url, string? cachedHtml,
        Dictionary<string, string> htmlCache, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(cachedHtml)) return cachedHtml;
        if (htmlCache.TryGetValue(url, out var hit)) return hit;
        if (_fetchHtml is null) return null;

        var html = await _fetchHtml(url, ct);
        if (html is not null) htmlCache[url] = html;
        return html;
    }

    // ---------------------------------------------------------------- writing

    private static readonly JsonSerializerOptions OutputJsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Writes the results: one JSON file per recipe inside <c>scrape/</c>.</summary>
    public static async Task WriteResultsAsync(List<RecipeResult> results,
        string outputDirectory, CancellationToken ct = default)
    {
        if (results.Count == 0) return;
        var dir = Path.Combine(outputDirectory, "scrape");
        Directory.CreateDirectory(dir);

        foreach (var r in results)
        {
            var fileName = string.Concat(r.Recipe.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
            var path = Path.Combine(dir, fileName + ".json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(r, OutputJsonOpts), ct);
        }
    }
}

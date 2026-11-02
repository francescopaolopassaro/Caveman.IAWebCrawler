using System.Text.Json;

namespace Caveman.IAWebCrawler.Extraction;

/// <summary>
/// One recipe field: how to read a piece of information from the page or item.
/// </summary>
public sealed class ScrapeField
{
    /// <summary>Output name of the field.</summary>
    public string Name { get; set; } = "";

    /// <summary>CSS selector relative to the item element (or to the page for page fields).</summary>
    public string? Selector { get; set; }

    /// <summary>
    /// When set, every match is collected in <see cref="ScrapedItem.Lists"/> and the
    /// joined (comma) value also lands in <see cref="ScrapedItem.Fields"/>; the
    /// field takes precedence over <see cref="Selector"/>.
    /// </summary>
    public string? MultipleSelector { get; set; }

    /// <summary>
    /// What to read from the matched element: "text" (default), "html",
    /// "innerhtml", "outerhtml" or an attribute name ("href", "src", "content"…).
    /// </summary>
    public string Attribute { get; set; } = "text";

    /// <summary>
    /// Optional regex applied to the raw value: when it matches, the first captured
    /// group wins (or the whole match when the pattern has no groups).
    /// </summary>
    public string? Regex { get; set; }

    /// <summary>Fallback value when the selector matches nothing (default: null).</summary>
    public string? Default { get; set; }

    /// <summary>Normalize a quoted/escaped attribute name ("@href" also accepted).</summary>
    public string NormalizedAttribute =>
        Attribute?.Trim().TrimStart('@').Trim().ToLowerInvariant() ?? "text";
}

/// <summary>
/// A declarative (repeatable) scraping recipe: pick items from the crawled pages
/// and read fields from each of them, everything described in one JSON file.
/// </summary>
public sealed class ScrapeRecipe
{
    /// <summary>Recipe name (also used for the output file name).</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// CSS selector of the repeated element (one item per match). Empty/omitted =
    /// a single item for the whole page.
    /// </summary>
    public string? ItemSelector { get; set; }

    /// <summary>Fields read from every item element. Optional.</summary>
    public List<ScrapeField> Fields { get; set; } = new();

    /// <summary>Fields read once per page and merged into every item. Optional.</summary>
    public List<ScrapeField> PageFields { get; set; } = new();

    /// <summary>
    /// Regex the page URL must match for this recipe to apply. Empty = every page.
    /// Example: "https://site\.com/news" (use Contains-equivalent patterns freely).
    /// </summary>
    public string? UrlPattern { get; set; }

    /// <summary>
    /// Optional CSS selector of the "next page" link for paginated listings:
    /// after the crawled pages, the recipe follows it (up to <see cref="MaxNextPages"/>,
    /// same host only) and scrapes the other pages of the list too.
    /// </summary>
    public string? NextPageSelector { get; set; }

    /// <summary>Maximum number of pagination pages fetched per recipe during scraping.</summary>
    public int MaxNextPages { get; set; } = 5;

    // ------------------------------------------------------------- loading

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Loads recipes from a folder (every *.json file) or from single files.</summary>
    public static List<ScrapeRecipe> LoadFrom(IEnumerable<string> paths)
    {
        var recipes = new List<ScrapeRecipe>();
        foreach (var path in paths.SelectMany(Expand))
        {
            try
            {
                var json = File.ReadAllText(path);
                List<ScrapeRecipe>? many = null;

                try
                {
                    many = JsonSerializer.Deserialize<List<ScrapeRecipe>>(json, JsonOpts);
                }
                catch (JsonException) { /* single-object file: fall through */ }

                if (many is { Count: > 0 })
                    recipes.AddRange(many.Where(r => r.Name.Length > 0));
                else if (JsonSerializer.Deserialize<ScrapeRecipe>(json, JsonOpts) is { } single &&
                         single.Name.Length > 0)
                    recipes.Add(single);
            }
            catch (Exception)
            {
                recipes.Add(new ScrapeRecipe { Name = $"[load error in {path}]" }); // surfaced to the caller
            }
        }
        return recipes;
    }

    private static IEnumerable<string> Expand(string path) =>
        Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.json", SearchOption.TopDirectoryOnly)
            : [path];
}

/// <summary>Result of applying one recipe to the crawled pages.</summary>
public sealed class RecipeResult
{
    /// <summary>Name of the recipe.</summary>
    public string Recipe { get; set; } = "";

    /// <summary>Items of every scanned page (LINQ-friendly flat list).</summary>
    public List<ScrapedItem> Items { get; set; } = new();

    /// <summary>The URLs of the HTML documents this recipe ran on.</summary>
    public List<string> PagesScanned { get; set; } = new();

    /// <summary>Additional pages discovered by the pagination selector.</summary>
    public List<string> PaginationUrls { get; set; } = new();
}

/// <summary>One scraped row: the fields of one item element.</summary>
public sealed class ScrapedItem
{
    /// <summary>Page URL the item was scraped from.</summary>
    public string SourceUrl { get; set; } = "";

    /// <summary>Single-value fields (selector / page fields).</summary>
    public Dictionary<string, string?> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Multi-value fields (multipleSelector).</summary>
    public Dictionary<string, List<string>> Lists { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

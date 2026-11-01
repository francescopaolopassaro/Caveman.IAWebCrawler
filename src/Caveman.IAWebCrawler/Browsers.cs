namespace Caveman.IAWebCrawler;

/// <summary>
/// Pre-defined browser profiles. Each entry pairs a real User-Agent with the
/// matching Accept header and client-hint headers (sec-ch-ua), when the engine
/// supports them. Real fingerprints greatly reduce the chance that anti-bot
/// filters block the crawl. Grouped by platform: Windows, macOS, Linux,
/// Android and iOS/iPadOS.
/// </summary>
public enum BrowserKind
{
    // --- Windows (PC) ---
    None,
    ChromeWindows,
    ChromeWindowsLt,      // previous Chromium build, still widespread
    EdgeWindows,
    FirefoxWindows,
    BraveWindows,

    // --- macOS ---
    SafariMacOS,
    SafariMacOSLt,
    ChromeMacOS,
    FirefoxMacOS,
    EdgeMacOS,

    // --- Linux ---
    ChromeLinux,
    EdgeLinux,
    FirefoxLinux,
    ChromiumLinux,

    // --- Android ---
    ChromeAndroid,
    FirefoxAndroid,
    EdgeAndroid,
    SamsungInternetAndroid,
    ChromeAndroidTablet,

    // --- iOS / iPadOS ---
    SafariIos,
    ChromeIos,
    EdgeIos,
    FirefoxIos,
    SafariIpadOS,

    /// <summary>No profile: <see cref="CrawlerOptions.UserAgent"/> is used as-is (alias of None/Custom).</summary>
    Custom = 0
}

/// <summary>A single browser identity: UA + Accept + client-hints.</summary>
/// <param name="Kind">Profile kind ("chrome-win", "safari-mac"...).</param>
/// <param name="Platform">Operating system family (Windows, macOS, Linux, Android, iOS).</param>
/// <param name="Ua">User-Agent header value.</param>
/// <param name="Accept">Accept header for HTML documents (null = engine default style).</param>
/// <param name="Hints">Extra identity headers (sec-ch-ua family); empty for engines without hints.</param>
public sealed record BrowserProfile(
    string Kind,
    string Platform,
    string Ua,
    string? Accept,
    IReadOnlyDictionary<string, string> Hints);

/// <summary>Catalogue of the 20 pre-built browser profiles.</summary>
public static class Browsers
{
    private const string HtmlAccept =
        "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8";

    private const string GeckoAccept =
        "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

    private static IReadOnlyDictionary<string, string> Hints(string brand, int major, string platform, string mobile) =>
        new Dictionary<string, string>
        {
            ["sec-ch-ua"] = $"\"Not_A Brand\";v=\"8\", \"Chromium\";v=\"{major}\", \"{brand}\";v=\"{major}\"",
            ["sec-ch-ua-mobile"] = mobile,
            ["sec-ch-ua-platform"] = $"\"{platform}\""
        };

    private static readonly Dictionary<string, string> NoHints =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>All 20 profiles, in catalogue order (Windows → macOS → Linux → Android → iOS).</summary>
    public static IReadOnlyList<BrowserKind> All { get; } =
    [
        BrowserKind.ChromeWindows, BrowserKind.ChromeWindowsLt, BrowserKind.EdgeWindows,
        BrowserKind.FirefoxWindows, BrowserKind.BraveWindows,
        BrowserKind.SafariMacOS, BrowserKind.SafariMacOSLt, BrowserKind.ChromeMacOS,
        BrowserKind.FirefoxMacOS, BrowserKind.EdgeMacOS,
        BrowserKind.ChromeLinux, BrowserKind.EdgeLinux, BrowserKind.FirefoxLinux, BrowserKind.ChromiumLinux,
        BrowserKind.ChromeAndroid, BrowserKind.FirefoxAndroid, BrowserKind.EdgeAndroid,
        BrowserKind.SamsungInternetAndroid, BrowserKind.ChromeAndroidTablet,
        BrowserKind.SafariIos, BrowserKind.ChromeIos, BrowserKind.EdgeIos,
        BrowserKind.FirefoxIos, BrowserKind.SafariIpadOS
    ];

    /// <summary>Resolves a profile. <see cref="BrowserKind.None"/>/<see cref="BrowserKind.Custom"/> return null.</summary>
    public static BrowserProfile? Get(BrowserKind kind) => kind switch
    {
        // ------------------------------------------------ --------- Windows
        BrowserKind.ChromeWindows => new(
            nameof(BrowserKind.ChromeWindows), "Windows 11",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
            HtmlAccept, Hints("Google Chrome", 140, "Windows", "?0")),

        BrowserKind.ChromeWindowsLt => new(
            nameof(BrowserKind.ChromeWindowsLt), "Windows 10",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
            HtmlAccept, Hints("Google Chrome", 131, "Windows", "?0")),

        BrowserKind.EdgeWindows => new(
            nameof(BrowserKind.EdgeWindows), "Windows 11",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0",
            HtmlAccept, Hints("Microsoft Edge", 140, "Windows", "?0")),

        BrowserKind.FirefoxWindows => new(
            nameof(BrowserKind.FirefoxWindows), "Windows 11",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:145.0) Gecko/20100101 Firefox/145.0",
            GeckoAccept, NoHints),

        BrowserKind.BraveWindows => new(
            nameof(BrowserKind.BraveWindows), "Windows 11",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
            HtmlAccept, Hints("Brave", 140, "Windows", "?0")),

        // ------------------------------------------------------------ macOS
        BrowserKind.SafariMacOS => new(
            nameof(BrowserKind.SafariMacOS), "macOS Sequoia",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Safari/605.1.15",
            GeckoAccept, NoHints),

        BrowserKind.SafariMacOSLt => new(
            nameof(BrowserKind.SafariMacOSLt), "macOS Ventura",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Safari/605.1.15",
            GeckoAccept, NoHints),

        BrowserKind.ChromeMacOS => new(
            nameof(BrowserKind.ChromeMacOS), "macOS Sequoia",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
            HtmlAccept, Hints("Google Chrome", 140, "macOS", "?0")),

        BrowserKind.FirefoxMacOS => new(
            nameof(BrowserKind.FirefoxMacOS), "macOS",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:145.0) Gecko/20100101 Firefox/145.0",
            GeckoAccept, NoHints),

        BrowserKind.EdgeMacOS => new(
            nameof(BrowserKind.EdgeMacOS), "macOS",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0",
            HtmlAccept, Hints("Microsoft Edge", 140, "macOS", "?0")),

        // ------------------------------------------------------------- Linux
        BrowserKind.ChromeLinux => new(
            nameof(BrowserKind.ChromeLinux), "Linux x86_64",
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
            HtmlAccept, Hints("Google Chrome", 140, "Linux", "?0")),

        BrowserKind.EdgeLinux => new(
            nameof(BrowserKind.EdgeLinux), "Linux x86_64",
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0",
            HtmlAccept, Hints("Microsoft Edge", 140, "Linux", "?0")),

        BrowserKind.FirefoxLinux => new(
            nameof(BrowserKind.FirefoxLinux), "Ubuntu Linux",
            "Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:145.0) Gecko/20100101 Firefox/145.0",
            GeckoAccept, NoHints),

        BrowserKind.ChromiumLinux => new(
            nameof(BrowserKind.ChromiumLinux), "Ubuntu Linux",
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chromium/141.0.0.0 Safari/537.36",
            HtmlAccept, Hints("Chromium", 141, "Linux", "?0")),

        // ------------------------------------------------------------ Android
        BrowserKind.ChromeAndroid => new(
            nameof(BrowserKind.ChromeAndroid), "Android (Pixel 8)",
            "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Mobile Safari/537.36",
            HtmlAccept, Hints("Google Chrome", 140, "Android", "?1")),

        BrowserKind.FirefoxAndroid => new(
            nameof(BrowserKind.FirefoxAndroid), "Android",
            "Mozilla/5.0 (Android 14; Mobile; rv:145.0) Gecko/145.0 Firefox/145.0",
            GeckoAccept, NoHints),

        BrowserKind.EdgeAndroid => new(
            nameof(BrowserKind.EdgeAndroid), "Android",
            "Mozilla/5.0 (Linux; Android 13; SM-S901B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Mobile Safari/537.36 EdgA/140.0.0.0",
            HtmlAccept, Hints("Microsoft Edge", 140, "Android", "?1")),

        BrowserKind.SamsungInternetAndroid => new(
            nameof(BrowserKind.SamsungInternetAndroid), "Android (Galaxy)",
            "Mozilla/5.0 (Linux; Android 14; SM-S928B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/27.0 Chrome/125.0.0.0 Mobile Safari/537.36",
            HtmlAccept, NoHints),

        BrowserKind.ChromeAndroidTablet => new(
            nameof(BrowserKind.ChromeAndroidTablet), "Android tablet",
            "Mozilla/5.0 (Linux; Android 14; SM-X710) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
            HtmlAccept, Hints("Google Chrome", 140, "Android", "?0")),

        // -------------------------------------------------------- iOS / iPadOS
        BrowserKind.SafariIos => new(
            nameof(BrowserKind.SafariIos), "iPhone",
            "Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Mobile/15E148 Safari/604.1",
            HtmlAccept, NoHints),

        BrowserKind.ChromeIos => new(
            nameof(BrowserKind.ChromeIos), "iPhone",
            "Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/141.0.0.0 Mobile/15E148 Safari/604.1",
            HtmlAccept, NoHints),

        BrowserKind.EdgeIos => new(
            nameof(BrowserKind.EdgeIos), "iPhone",
            "Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 EdgiOS/141.2410.0 Mobile/15E148 Safari/605.1.15",
            HtmlAccept, NoHints),

        BrowserKind.FirefoxIos => new(
            nameof(BrowserKind.FirefoxIos), "iPhone",
            "Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) FxiOS/142.0 Mobile/15E148 Safari/605.1.15",
            HtmlAccept, NoHints),

        BrowserKind.SafariIpadOS => new(
            nameof(BrowserKind.SafariIpadOS), "iPad",
            "Mozilla/5.0 (iPad; CPU OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Mobile/15E148 Safari/604.1",
            HtmlAccept, NoHints),

        BrowserKind.None or BrowserKind.Custom => null,
        _ => null
    };

    /// <summary>Friendly lookup used by CLIs: case-insensitive, tolerant names.</summary>
    public static bool TryParseKind(string? value, out BrowserKind kind)
    {
        kind = BrowserKind.ChromeWindows;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var v = value.Trim().Replace("-", "").Replace("_", "").Replace(" ", "");

        // legacy / shorthand aliases
        var alias = v.ToLowerInvariant() switch
        {
            "chrome" => "chromewindows",
            "edge" => "edgewindows",
            "firefox" => "firefoxwindows",
            "safari" => "safarimacos",
            "mobile" => "chromeandroid",
            "none" => "none",
            "custom" => "custom",
            _ => v.ToLowerInvariant()
        };

        foreach (var k in All)
        {
            var name = k.ToString().ToLowerInvariant();
            if (name == alias || (alias.Length > 0 && name.StartsWith(alias) is true && alias.Length >= 5))
            {
                kind = k;
                return true;
            }
        }
        if (alias is "none" or "custom") { kind = BrowserKind.None; return true; }
        return false;
    }
}

/// <summary>
/// Catalogue of 40 ready-to-use <c>Accept-Language</c> values. Each entry builds a
/// header like "it-IT,it;q=0.9,en;q=0.8" — locale, fallback language and English
/// as final net — exactly the format browsers send.
/// </summary>
public static class AcceptLanguages
{
    /// <summary>The 40 supported languages (BCP-47 codes).</summary>
    public static IReadOnlyList<string> Codes { get; } =
    [
        "it-IT", "en-US", "en-GB", "de-DE", "fr-FR", "es-ES", "pt-PT", "pt-BR",
        "nl-NL", "ru-RU", "pl-PL", "uk-UA", "cs-CZ", "sk-SK", "hu-HU", "ro-RO",
        "bg-BG", "el-GR", "hr-HR", "sl-SI", "sr-RS", "tr-TR", "da-DK", "sv-SE",
        "nb-NO", "fi-FI", "ar-SA", "he-IL", "fa-IR", "hi-IN", "ur-PK", "bn-BD",
        "zh-CN", "zh-TW", "ja-JP", "ko-KR", "th-TH", "vi-VN", "id-ID", "ms-MY"
    ];

    /// <summary>Builds the Accept-Language header for a code found in <see cref="Codes"/>.</summary>
    public static string? HeaderFor(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim();

        // already a full header ("it-IT,it;q=0.9,en;q=0.8") → pass through
        if (c.Contains(';') && c.Contains(',')) return c;

        // short language ("it", "en") → pick the first catalogue entry for it
        var exact = Codes.FirstOrDefault(k => k.Equals(c, StringComparison.OrdinalIgnoreCase));
        if (exact is null)
        {
            var prefix = c.Split('-')[0].ToLowerInvariant();
            exact = Codes.FirstOrDefault(k => k.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase));
        }
        if (exact is null) return c;

        var parts = exact.Split('-');
        return $"{exact},{parts[0].ToLowerInvariant()};q=0.9,en;q=0.8";
    }

    /// <summary>True when the code (or its prefix) exists in the catalogue.</summary>
    public static bool IsSupported(string code) =>
        Codes.Any(k => k.Equals(code, StringComparison.OrdinalIgnoreCase) ||
                       k.StartsWith(code.Split('-')[0] + "-", StringComparison.OrdinalIgnoreCase));
}

using System.Text;

namespace Caveman.IAWebCrawler.Output;

/// <summary>
/// De-minifies (beautifies) the JS and CSS files of the archive: restores newlines
/// and indentation after {, } and ; while recognizing strings ('"), comments
/// (//, /* */), template literals with their ${...} expressions and regex
/// literals (never broken). Files already readable (newline/line-length
/// heuristics) are left untouched.
/// </summary>
public static class AssetBeautifier
{
    public static void Beautify(IReadOnlyList<ResourceInfo> resources, string outputDirectory, Action<string>? log = null)
    {
        var filesRoot = Path.Combine(outputDirectory, "files");
        int js = 0, css = 0;

        foreach (var r in resources)
        {
            if (r.Error is not null || r.LocalPath.Length == 0) continue;
            var full = Path.Combine(filesRoot, r.LocalPath);
            if (!File.Exists(full)) continue;

            var ext = Path.GetExtension(full).ToLowerInvariant();
            try
            {
                if (ext is ".js" or ".mjs")
                {
                    if (BeautifyJs(File.ReadAllText(full)) is { } beautified)
                    { File.WriteAllText(full, beautified); js++; }
                }
                else if (ext == ".css")
                {
                    if (BeautifyCss(File.ReadAllText(full)) is { } beautifiedCss)
                    { File.WriteAllText(full, beautifiedCss); css++; }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"  ✗ beautify failed on {r.LocalPath}: {ex.Message}");
            }
        }

        log?.Invoke($"  De-minified: {js} .js, {css} .css");
    }

    // ------------------------------------------------------------------- js

    internal static string? BeautifyJs(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || AlreadyReadable(source)) return null;

        var sb = new StringBuilder(source.Length + source.Length / 3);
        int depth = 0;
        char quote = '\0';               // current string ('' | "" | ` template)
        bool inLineComment = false, inBlockComment = false;
        bool inRegex = false, inRegexClass = false;
        bool inTplExpr = false;          // inside a template ${...} expression
        int braceBalance = 0;            // brace nesting of the ${...} expression
        char prevMeaningful = '\0';      // last non-space code char (regex detection)

        // a '/' opens a regex literal only after these characters
        static bool RegexAllowed(char c) => "\0()[{};=:!?&|+<>^~/*%-\n\r".Contains(c);

        void Break()
        {
            TrimEnd(sb);
            sb.AppendLine();
            sb.Append(' ', depth * 2);
        }

        for (int i = 0; i < source.Length; i++)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (inLineComment)
            {
                sb.Append(c);
                if (c is '\n' or '\r' && !(c == '\r' && next == '\n')) inLineComment = false;
                continue;
            }
            if (inBlockComment)
            {
                sb.Append(c);
                if (c == '*' && next == '/') { sb.Append('/'); i++; inBlockComment = false; }
                continue;
            }
            if (inRegex)
            {
                // regex literals are copied verbatim: no newline can be inserted
                // inside them or at / inside a character class ([///] stays intact)
                sb.Append(c);
                if (c == '\\') { if (next != '\0') { sb.Append(next); i++; } continue; }
                if (inRegexClass) { if (c == ']') inRegexClass = false; continue; }
                if (c == '[') { inRegexClass = true; continue; }
                if (c == '/') { inRegex = false; prevMeaningful = c; }
                continue;
            }

            // ordinary single/double-quoted strings
            if (quote is '"' or '\'')
            {
                sb.Append(c);
                if (c == '\\') { if (next != '\0') { sb.Append(next); i++; } continue; }
                if (c == quote) quote = '\0';
                else if (c == '\n') quote = '\0'; // unterminated string: restart
                continue;
            }

            // template literal, string part between backticks
            if (quote == '`' && !inTplExpr)
            {
                if (c == '$' && next == '{')
                {
                    sb.Append("${"); i++;
                    inTplExpr = true;
                    braceBalance = 0;
                    prevMeaningful = c;      // '${' still ends with '{'
                    continue;
                }
                sb.Append(c);
                if (c == '\\') { if (next != '\0') { sb.Append(next); i++; } continue; }
                if (c == '`') quote = '\0';
                continue;
            }

            switch (c)
            {
                case '"':
                    quote = '"'; sb.Append(c); break;

                case '\'':
                    quote = '\''; sb.Append(c); break;

                case '`':
                    // start of a template (or, deep inside, a nested one: back in
                    // string mode either way when the expression accounting is 0)
                    quote = '`';
                    sb.Append(c);
                    break;

                case '/':
                    if (next == '/') { sb.Append("//"); i++; inLineComment = true; break; }
                    if (next == '*') { sb.Append("/*"); i++; inBlockComment = true; break; }
                    if (RegexAllowed(prevMeaningful))
                    {
                        inRegex = true;
                        sb.Append(c);
                        prevMeaningful = c;
                        break;
                    }
                    sb.Append(c); // plain division
                    prevMeaningful = c;
                    break;

                case '{':
                    if (inTplExpr) braceBalance++;
                    depth++;
                    sb.Append('{');
                    prevMeaningful = '{';
                    if (!inTplExpr) Break();
                    break;

                case '}':
                    if (inTplExpr && braceBalance == 0)
                    {
                        // closes the ${...} expression: back to the template string
                        sb.Append('}');
                        inTplExpr = false;
                        prevMeaningful = '}';
                        break;
                    }
                    if (inTplExpr) braceBalance--;
                    depth = Math.Max(0, depth - 1);
                    TrimEnd(sb);
                    sb.AppendLine();
                    sb.Append(' ', depth * 2);
                    sb.Append('}');
                    prevMeaningful = '}';
                    if (!inTplExpr) Break();
                    break;

                case ';':
                    sb.Append(';');
                    prevMeaningful = ';';
                    if (!inTplExpr) Break();
                    break;

                case '\n' or '\r':
                    break; // original newlines already rebuilt by the beautifier

                default:
                    sb.Append(c);
                    if (!char.IsWhiteSpace(c)) prevMeaningful = c;
                    break;
            }
        }

        TrimEnd(sb);
        sb.AppendLine();
        return sb.ToString();
    }

    // ------------------------------------------------------------------ css

    internal static string? BeautifyCss(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || AlreadyReadable(source)) return null;

        var sb = new StringBuilder(source.Length + source.Length / 3);
        int depth = 0;
        char quote = '\0';
        bool inComment = false;

        void Break()
        {
            TrimEnd(sb);
            sb.AppendLine();
            sb.Append(' ', depth * 2);
        }

        for (int i = 0; i < source.Length; i++)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (inComment)
            {
                sb.Append(c);
                if (c == '*' && next == '/') { sb.Append('/'); i++; inComment = false; Break(); }
                continue;
            }
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == '\\') { if (next != '\0') { sb.Append(next); i++; } continue; }
                if (c == quote) quote = '\0';
                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    sb.Append(c);
                    break;

                case '/':
                    if (next == '*') { sb.Append("/*"); i++; inComment = true; break; }
                    sb.Append(c);
                    break;

                case '{':
                    depth++;
                    TrimEnd(sb);
                    sb.Append(" {");
                    Break();
                    break;

                case '}':
                    depth = Math.Max(0, depth - 1);
                    TrimEnd(sb);
                    sb.AppendLine();
                    sb.Append(' ', depth * 2);
                    sb.Append('}');
                    Break();
                    break;

                case ';':
                    sb.Append(';');
                    Break();
                    break;

                case '\n' or '\r':
                    break;

                default:
                    sb.Append(c);
                    break;
            }
        }

        TrimEnd(sb);
        sb.AppendLine();
        return sb.ToString();
    }

    // ------------------------------------------------------------ utilities

    /// <summary>Heuristic: frequent newlines and short average lines = already readable code.</summary>
    private static bool AlreadyReadable(string source)
    {
        int scan = Math.Min(source.Length, 50000);
        int nl = 0;
        for (int i = 0; i < scan; i++)
            if (source[i] == '\n') nl++;

        double bytesPerLine = nl > 0 ? scan / (double)nl : double.MaxValue;
        return nl > 10 && bytesPerLine < 200; // already formatted: left untouched
    }

    private static void TrimEnd(StringBuilder sb)
    {
        int end = sb.Length;
        while (end > 0 && (sb[end - 1] == ' ' || sb[end - 1] == '\t')) end--;
        sb.Length = end;
    }
}

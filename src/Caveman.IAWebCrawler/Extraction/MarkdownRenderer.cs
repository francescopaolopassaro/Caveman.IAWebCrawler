using AngleSharp.Dom;

namespace Caveman.IAWebCrawler.Extraction;

/// <summary>
 /// Converts an HTML page into clean Markdown, keeping the document order:
 /// headings, paragraphs, lists, tables, blockquotes, code blocks, links and inline images.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly HashSet<string> SkipTags = new(StringComparer.OrdinalIgnoreCase)
    { "SCRIPT", "STYLE", "NOSCRIPT", "TEMPLATE", "NAV", "IFRAME" };

    public static string Render(IDocument doc, Uri pageUrl)
    {
        var sb = new System.Text.StringBuilder();
        if (doc.Body is not null)
            foreach (var node in doc.Body.ChildNodes)
                WalkNode(node, sb, pageUrl, 0, inList: false, inPre: false);
        var md = sb.ToString();
         // collapse more than two consecutive line breaks
        while (md.Contains("\n\n\n\n")) md = md.Replace("\n\n\n\n", "\n\n\n");
        return md.Trim();
    }

    private static void WalkNode(INode node, System.Text.StringBuilder sb, Uri pageUrl, int indent, bool inList, bool inPre)
    {
        switch (node.NodeType)
        {
            case NodeType.Text:
            {
                var text = ((IText)node).Data;
                if (string.IsNullOrWhiteSpace(text)) return;
                // clean template leftovers like "{{X}}" / stray braces from CMS copy
                if (text.Contains("{{")) text = text.Replace("{{", "").Replace("}}", "");
                // markup often swallows whitespace at element boundaries
                if (!inPre && sb.Length > 0)
                {
                    var last = sb[^1];
                    var first = text.Trim().Length > 0 ? text.Trim()[0] : '\0';
                    if (char.IsLetter(last) && char.IsLetter(first)) sb.Append(' ');
                }
                sb.Append(inPre ? text : Collapse(text));
                return;
            }
            case NodeType.Element:
                break;
            default:
                return;
        }

        var el = (IElement)node;
        var tag = el.TagName.ToUpperInvariant();
        if (SkipTags.Contains(tag)) return;
        if (el.HasAttribute("aria-hidden") && el.GetAttribute("aria-hidden") == "true") return;

        switch (tag)
        {
            case "HEADER": break; // niente banner: il contenuto reale e' nel body
            case "H1": case "H2": case "H3": case "H4": case "H5": case "H6":
                var lvl = tag[1] - '0';
                sb.Append('\n').Append('#', lvl).Append(' ').AppendLine(Collapse(el.TextContent));
                return;

            case "P":
                if (el.TextContent.Length > 0)
                {
                    WalkChildren(el, sb, pageUrl, indent, inList, inPre);
                    sb.Append("\n\n");
                }
                return;

            case "UL": case "OL":
                WriteList(el, sb, pageUrl, indent, ordered: tag == "OL", inPre);
                sb.Append('\n');
                return;

            case "LI":
                sb.Append(' ', indent * 2).Append("- ").AppendLine(Collapse(el.TextContent));
                return;

            case "TABLE":
                WriteTable(el, sb);
                sb.Append('\n');
                return;

            case "PRE":
                sb.Append("\n```\n").Append(el.TextContent.TrimEnd()).Append("\n```\n\n");
                return;

            case "BLOCKQUOTE":
                foreach (var line in Collapse(el.TextContent).Split('\n'))
                    sb.Append("> ").AppendLine(line.Trim());
                sb.Append('\n');
                return;

            case "BR":
                sb.AppendLine();
                return;

            case "HR":
                sb.Append("\n---\n\n");
                return;

            case "IMG":
                WriteImage(el, sb, pageUrl);
                sb.Append('\n');
                return;

            case "FIGURE":
                WalkChildren(el, sb, pageUrl, indent, inList, inPre);
                var cap = el.QuerySelector("figcaption")?.TextContent;
                if (!string.IsNullOrWhiteSpace(cap)) sb.Append('*').AppendLine(Collapse(cap)).AppendLine("*");
                sb.Append('\n');
                return;

            case "A":
            {
                var href = el.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(href)) { WalkChildren(el, sb, pageUrl, indent, inList, inPre); return; }
                var abs = Uri.TryCreate(pageUrl, href, out var u) ? u.ToString() : href;
                sb.Append('[');
                WalkChildren(el, sb, pageUrl, indent, inList, inPre);
                sb.Append(']').Append('(').Append(abs).Append(')');
                return;
            }

            case "SVG":
            {
                // inline vector icon/logo: dump a short textual note instead of raw markup
                var title = Collapse(el.QuerySelector("title")?.TextContent);
                var desc = Collapse(el.QuerySelector("desc")?.TextContent);
                var text = Collapse(string.Join(", ",
                    el.QuerySelectorAll("text, tspan").Select(t => t.TextContent)
                        .Where(t => !string.IsNullOrWhiteSpace(t))));
                var note = title.Length > 0 ? title : desc.Length > 0 ? desc : "";
                if (note.Length == 0 && text.Length > 0) note = text;
                sb.Append(string.IsNullOrEmpty(note)
                    ? "*![inline svg graphic]*"
                    : $"*![{note} (inline svg)]*");
                sb.Append('\n');
                return;
            }

            default:
                WalkChildren(el, sb, pageUrl, indent, inList, inPre);
                if (tag is "DIV" or "SECTION" or "ARTICLE" or "MAIN" or "DETAILS" or "ASIDE" or "FOOTER")
                    sb.Append('\n');
                return;
        }
    }

    private static void WalkChildren(IElement el, System.Text.StringBuilder sb, Uri pageUrl, int indent, bool inList, bool inPre)
    {
        foreach (var child in el.ChildNodes)
            WalkNode(child, sb, pageUrl, indent, inList, inPre);
    }

    private static void WriteList(IElement el, System.Text.StringBuilder sb, Uri pageUrl, int indent, bool ordered, bool inPre)
    {
        int i = 1;
        foreach (var li in el.Children.Where(c => c.TagName.Equals("li", StringComparison.OrdinalIgnoreCase)))
        {
            var bullet = ordered ? $"{i++}. " : "- ";
             // item text without the text of the possibly nested sub-lists
            var clone = (IElement)li.Clone(true);
            foreach (var sub in clone.QuerySelectorAll("ul,ol")) sub.Remove();
            var inline = Collapse(clone.TextContent);
            sb.Append(' ', indent * 2).Append(bullet).AppendLine(inline);
            foreach (var sub in li.Children.Where(c =>
                         c.TagName.Equals("ul", StringComparison.OrdinalIgnoreCase) ||
                         c.TagName.Equals("ol", StringComparison.OrdinalIgnoreCase)))
                WriteList(sub, sb, pageUrl, indent + 1, sub.TagName.Equals("ol", StringComparison.OrdinalIgnoreCase), inPre);
        }
    }

    private static void WriteImage(IElement el, System.Text.StringBuilder sb, Uri pageUrl)
    {
        var src = el.GetAttribute("src");
        if (string.IsNullOrWhiteSpace(src)) return;
        var abs = Uri.TryCreate(pageUrl, src, out var u) ? u.ToString() : src;
        var alt = Collapse(el.GetAttribute("alt") ?? "");
        sb.Append("![")
          .Append(string.IsNullOrEmpty(alt) ? "image" : alt)
          .Append("](").Append(abs).Append(')');
    }

    private static void WriteTable(IElement el, System.Text.StringBuilder sb)
    {
        var rows = el.QuerySelectorAll("tr")
            .Select(tr => tr.QuerySelectorAll("th,td").Select(c => MdEscape(Collapse(c.TextContent))).ToList())
            .Where(r => r.Count > 0)
            .ToList();
        if (rows.Count == 0) return;

        int cols = rows.Max(r => r.Count);
         // heading: first line
        var header = rows[0];
        for (int i = 0; i < cols; i++)
            if (i >= header.Count) header.Add("");

        sb.Append('|');
        foreach (var c in header) sb.Append(' ').Append(c).Append(" |");
        sb.AppendLine();
        sb.Append('|').Append(string.Join('|', Enumerable.Repeat(" --- ", cols))).AppendLine();

        foreach (var row in rows.Skip(1))
        {
            sb.Append('|');
            for (int i = 0; i < cols; i++)
                sb.Append(' ').Append(i < row.Count ? row[i] : "").Append(" |");
            sb.AppendLine();
        }
        sb.AppendLine();
    }

    private static readonly System.Text.RegularExpressions.Regex WhitespaceRegex =
        new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string Collapse(string? s) => WhitespaceRegex.Replace(s ?? "", " ").Trim();

    private static string MdEscape(string s) =>
        s.Replace("|", "\\|").Replace("\n", " ");
}

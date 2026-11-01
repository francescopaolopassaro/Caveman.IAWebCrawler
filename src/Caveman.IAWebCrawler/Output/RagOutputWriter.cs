using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Caveman.IAWebCrawler.Extraction;

namespace Caveman.IAWebCrawler.Output;

/// <summary>
 /// Prepares the outputs designed for LLM/RAG pipelines:
/// <list type="bullet">
 /// <item><c>rag/chunks.jsonl</c> - text chunks of the <b>pages</b>, but also of the site
 ///       <b>documents</b> (pdf/docx/csv/json: text extracted with Stilo.Documents)
 ///       and <b>images</b> (jpg/jpeg/png: metadata, alt description, size - no OCR),
 ///       with hash, token estimate and duplicate flag: embedding-ready format.</item>
 /// <item><c>rag/pages.jsonl</c> - one JSON row per page (url, title, description, headings).</item>
 /// <item><c>rag/corpus.txt</c> - one-source-per-block text corpus for long prompts.</item>
 /// <item><c>rag/manifest.json</c> - statistics and instructions for the embedding pipeline.</item>
/// </list>
 /// Every chunk carries <c>sourceType</c>: page | document | image.
/// </summary>
public static class RagOutputWriter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

     /// <summary>Source of a chunk: web page, document or image.</summary>
    private sealed record ChunkSource(
        string Url, string Title, string SourceType,
        string? FilePath, string? DocumentType, DateTime CrawledAtUtc);

    public static async Task WriteAsync(SiteCrawlResult result, CrawlerOptions options, CancellationToken ct = default)
    {
        var dir = Path.Combine(options.OutputDirectory, "rag");
        Directory.CreateDirectory(dir);
        _dedup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _seq = 0;

        var pages = result.Pages.Where(p => p.Error is null).ToList();
        var chunks = new List<RagChunk>();

         // ---- 1) chunks of the HTML pages ------------------------------------
        foreach (var page in pages)
        {
            var src = new ChunkSource(page.Url, page.Title, "page", null, null, page.CrawledAtUtc);
            foreach (var (headingPath, text) in SplitByHeadings(page))
                chunks.AddRange(MakeChunks(src, headingPath, text,
                    options.RagChunkSize, options.RagChunkOverlap));
        }
        int pageChunks = chunks.Count;

         // ---- 2) chunks of the site documents (pdf/docx/csv/json, Stilo text)
        int docChunks = 0, docsSeen = 0;
        if (options.RagIncludeDocuments)
        {
            foreach (var r in result.Resources.Where(r =>
                         r.Type == ResourceType.Document && r.Error is null))
            {
                docsSeen++;
                var ext = ExtOf(r.Url).ToLowerInvariant();
                var title = TitleOfUrl(r.Url);
                string? filePath = r.LocalPath.Length > 0 ? r.LocalPath : null;
                var src = new ChunkSource(r.Url, title, "document", filePath, ext, result.FinishedAtUtc);

                var text = r.ExtractedText;
                 // in Files mode the text comes from the file itself (document already on disk)
                if (string.IsNullOrWhiteSpace(text) && filePath is not null)
                {
                    var full = Path.Combine(options.OutputDirectory, "files", filePath);
                    if (File.Exists(full))
                    {
                        try
                        {
                            text = await DocumentTextExtractor.ExtractAsync(
                                await File.ReadAllBytesAsync(full, ct),
                                ext, r.ContentType, options.MaxDocumentTextChars, ct);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  ✗ RAG extraction from {filePath}: {ex.Message}");
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                     var heading = new List<string> { $"Document {ext.ToUpperInvariant()} - {title}" };
                    chunks.AddRange(MakeChunks(src, heading, text,
                        options.RagChunkSize, options.RagChunkOverlap));
                    docChunks = chunks.Count - pageChunks;
                }
                else
                {
                     // format with no extractable text: a marker chunk is still useful in the RAG
                     chunks.Add(Build(src, new List<string> { $"Document {ext.ToUpperInvariant()} - {title}" },
                        $"Site document not extractable as text (format {ext}): «{title}». " +
                        $"Downloadable from {r.Url}." +
                        (filePath is not null ? $" Local archive copy: files/{filePath}." : ""), ct));
                }
            }
        }

        // ---- 3) chunk delle immagini del sito (jpeg/jpg/png + webp/gif/svg ecc.)
        int imgChunks = 0, imagesSeen = 0;
        if (options.RagIncludeImages)
        {
            // alt/descrizioni dichiarate nelle pagine
            var alt2 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var page in pages)
                foreach (var img in page.Images)
                    if (!string.IsNullOrWhiteSpace(img.Alt))
                        alt2.TryAdd(NormUrl(img.Src), img.Alt);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in result.Resources.Where(r =>
                         r.Type is ResourceType.Image && r.Error is null))
            {
                if (!seen.Add(NormUrl(r.Url))) continue;
                imagesSeen++;

                string? filePath = r.LocalPath.Length > 0 ? r.LocalPath : null;
                string? dims = null;
                if (filePath is not null)
                {
                    var full = Path.Combine(options.OutputDirectory, "files", filePath);
                    if (File.Exists(full))
                        dims = ImageDimensions(await File.ReadAllBytesAsync(full, ct));
                }

                alt2.TryGetValue(NormUrl(r.Url), out var alt);
                var src = new ChunkSource(r.Url, TitleOfUrl(r.Url), "image", filePath, ExtOf(r.Url), result.FinishedAtUtc);
                var text =
                     $"Site image{(alt?.Length > 0 ? $" - '{alt}'" : "")}. " +
                    $"Format: {ExtOf(r.Url).ToUpperInvariant()}. Published at: {r.Url}." +
                    (dims is not null ? $" Dimensions: {dims} (width x height)." : "") +
                    (r.LengthBytes > 0 ? $" File size: {r.LengthBytes / 1024.0:F0} KB." : "") +
                    (filePath is not null ? $" Local copy: files/{filePath}." : "");

                 chunks.Add(Build(src, new List<string> { "Site images" }, text, ct));
            }
            imgChunks = chunks.Count - pageChunks - docChunks;
        }

        // ---- chunks.jsonl ---------------------------------------------------
        var chunksPath = Path.Combine(dir, "chunks.jsonl");
        await using (var fs = File.Create(chunksPath))
        {
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                await JsonSerializer.SerializeAsync(fs, chunk, JsonOpts, ct);
                await fs.WriteAsync("\n"u8.ToArray(), ct);
            }
        }

        // ---- pages.jsonl ----------------------------------------------------
        var pagesPath = Path.Combine(dir, "pages.jsonl");
        await using (var fs = File.Create(pagesPath))
        {
            foreach (var page in pages)
            {
                ct.ThrowIfCancellationRequested();
                var dto = new
                {
                    url = page.Url,
                    title = page.Title,
                    description = page.MetaTags.TryGetValue("description", out var d) ? d : null,
                    language = page.Language,
                    depth = page.Depth,
                    crawledAtUtc = page.CrawledAtUtc,
                    markdownLength = page.Markdown.Length,
                    headings = page.Headings.Select(h => $"{new string('#', h.Level)} {h.Text}").ToList(),
                    nLinks = page.Links.Count,
                    nImages = page.Images.Count
                };
                await JsonSerializer.SerializeAsync(fs, dto, JsonOpts, ct);
                await fs.WriteAsync("\n"u8.ToArray(), ct);
            }
        }

        // ---- corpus.txt (pagine + documenti con testo) ----------------------
        var corpusPath = Path.Combine(dir, "corpus.txt");
        await using (var fs = File.Create(corpusPath))
        {
            using var writer = new StreamWriter(fs, new UTF8Encoding(false));
            foreach (var page in pages)
            {
                var body = string.IsNullOrWhiteSpace(page.Markdown)
                    ? string.Join("\n\n", page.Paragraphs)
                    : page.Markdown;
                 writer.WriteLine($"===== PAGE: {page.Title} =====");
                writer.WriteLine($"URL: {page.Url}");
                writer.WriteLine();
                writer.WriteLine(body);
                writer.WriteLine();
            }

            if (options.RagIncludeDocuments)
                foreach (var chunk in chunks.Where(c => c.SourceType == "document" && c.DuplicateOf is null))
                {
                     writer.WriteLine($"===== DOCUMENT: {chunk.Title} =====");
                    writer.WriteLine($"URL: {chunk.Url}");
                    writer.WriteLine();
                    writer.WriteLine(chunk.Text);
                    writer.WriteLine();
                }
            await writer.FlushAsync(ct);
        }

        // ---- manifest.json ---------------------------------------------------
        var docList = chunks.Where(c => c.SourceType == "document").ToList();
        var manifest = new
        {
            generator = "Caveman.IAWebCrawler",
            generatedAtUtc = DateTime.UtcNow,
            site = result.StartUrl,
            pages = pages.Count,
            documents = docsSeen,
            images = imagesSeen,
            chunks = chunks.Count,
            chunksBySource = new { page = pageChunks, document = docChunks, image = imgChunks },
            documentsWithText = docList.Count(c => c.DuplicateOf is null),
            documentsTextChars = docList.Sum(c => c.Text.Length),
            chunksDeduplicated = chunks.Count(c => c.DuplicateOf is not null),
            estimatedTokens = chunks.Sum(c => c.Tokens),
            chunkTargetChars = options.RagChunkSize,
            chunkOverlapChars = options.RagChunkOverlap,
            suggestedEmbedding = new
            {
                note = "Each row of chunks.jsonl is self-contained for the embedding: " +
                         "use the text and the metadata (sourceType, documentType, headingPath, url) as filters. " +
                         "null documentType = page or image chunk.",
                 deduplicationRule = "Chunks with the same textHash carry duplicateOf pointing at the first: " +
                                     "keep one chunk per hash when indexing."
            },
            files = new
            {
                chunks = "chunks.jsonl",
                pages = "pages.jsonl",
                corpus = "corpus.txt",
                markdownPerPage = "../markdown/",
                llmsFull = "../markdown/llms-full.txt",
                rawArchive = "../files/"
            }
        };
        await using (var fs = File.Create(Path.Combine(dir, "manifest.json")))
            await JsonSerializer.SerializeAsync(fs, manifest, JsonOpts, ct);

        Console.WriteLine($"  → RAG: {chunks.Count} chunks (pages {pageChunks}, documents {docChunks} out of {docsSeen}, " +
                          $"images {imgChunks} out of {imagesSeen}) → {Path.GetFullPath(dir)}");
    }

     // ------------------------------------------------------------ utilities

    /// <summary>
     /// Splits the text into sections according to the headings (for pages, the Markdown);
     /// headingPath is the breadcrumb of the titles leading to each section.
    /// </summary>
    private static List<(List<string> HeadingPath, string Text)> SplitByHeadings(PageResult page)
    {
        var sections = new List<(List<string>, string)>();
        var path = new List<string>();
        var text = new StringBuilder();

        void Flush()
        {
            var body = text.ToString().Trim();
            if (body.Length > 0)
                sections.Add((new List<string>(path), body));
        }

        var source = page.Markdown.Length > 0 ? page.Markdown : string.Join("\n\n", page.Paragraphs);
        foreach (var line in source.Split('\n'))
        {
            var heading = TryHeading(line);
            if (heading is not null)
            {
                Flush();
                var (level, content) = heading.Value;
                while (path.Count >= level) path.RemoveAt(path.Count - 1);
                path.Add(content);
                text.Clear();
                 text.AppendLine(line); // the heading stays inside the chunk text
            }
            else
            {
                text.AppendLine(line);
            }
        }
        Flush();
        return sections;
    }

     /// <summary>"## Title" -> (2, "Title"); null when the line is not a Markdown heading.</summary>
    private static (int Level, string Text)? TryHeading(string line)
    {
        if (line.Length < 2 || line[0] != '#') return null;
        int level = 0;
        while (level < line.Length && line[level] == '#') level++;
        if (level > 6 || level >= line.Length || line[level] != ' ') return null;
        return (level, line[(level + 1)..].Trim());
    }

    /// <summary>
    /// Splits a text block into chunk sizes at most targetSize, breaking at paragraph boundaries
    /// and repeating the tail of the previous chunk for the requested overlap.
    /// </summary>
    private static IEnumerable<RagChunk> MakeChunks(ChunkSource src, List<string> headingPath, string text,
        int targetSize, int overlap)
    {
        var paragraphs = text.Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries)
                             .Select(p => p.Trim())
                             .Where(p => p.Length > 0);

        var buf = new StringBuilder();
        foreach (var p in paragraphs)
        {
            if (p.Length > targetSize)
            {
                if (buf.Length > 0)
                {
                    yield return Build(src, headingPath, buf, null);
                    buf.Clear();
                }
                foreach (var part in SplitLong(p, targetSize, overlap))
                    yield return Build(src, headingPath, new StringBuilder(part), null);
                continue;
            }

            if (buf.Length > 0 && buf.Length + p.Length + 2 > targetSize)
            {
                yield return Build(src, headingPath, buf, null);
                 // overlap: restart from the tail of the previous chunk
                var tail = buf.ToString().TrimEnd();
                var lastStart = tail.LastIndexOf("\n\n", StringComparison.Ordinal);
                var overlapText = lastStart > 0
                    ? tail[(lastStart + 2)..].Trim()
                    : (tail.Length <= overlap * 3 ? tail : "");

                buf.Clear();
                if (overlap > 0 && overlapText.Length > 0 && overlapText.Length <= overlap * 3)
                    buf.Append(overlapText).Append("\n\n");
            }

            buf.Append(p).Append("\n\n");
        }

        if (buf.Length > 0)
            yield return Build(src, headingPath, buf, null);
    }

    private static IEnumerable<string> SplitLong(string p, int target, int overlap)
    {
        int step = Math.Max(1, target - overlap);
        for (int pos = 0; pos < p.Length; pos += step)
        {
            var len = Math.Min(target, p.Length - pos);
            yield return p.Substring(pos, len);
            if (pos + len >= p.Length) yield break;
        }
    }

    private static RagChunk Build(ChunkSource src, List<string> headingPath, string content, CancellationToken ct)
        => Build(src, headingPath, new StringBuilder(content), null);

    private static RagChunk Build(ChunkSource src, List<string> headingPath, StringBuilder content, CancellationToken? _)
    {
        var text = content.ToString().Trim();
        var hash = Hash(text);

        string? duplicateOf = null;
        var id = $"{hash[..12].ToLowerInvariant()}-{_seq++:0000}";
        if (_dedup!.TryGetValue(hash, out var firstId))
            duplicateOf = firstId;
        else
            _dedup[hash] = id;

        return new RagChunk
        {
            Id = id,
            Url = src.Url,
            Title = src.Title,
            SourceType = src.SourceType,
            FilePath = src.FilePath,
            DocumentType = src.DocumentType,
            HeadingPath = headingPath.Where(h => h.Length > 0).ToList(),
            Text = text,
            Tokens = Math.Max(1, text.Length / 4),
            TextHash = hash,
            DuplicateOf = duplicateOf,
            CrawledAtUtc = src.CrawledAtUtc
        };
    }

     // ------------------------------------------------------------ utilities

    private static int _seq;
    private static Dictionary<string, string>? _dedup;

    private static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NormUrl(string url)
    {
        try { return new Uri(url).GetLeftPart(UriPartial.Query).ToString(); }
        catch { return url; }
    }

    private static string ExtOf(string url)
    {
        try
        {
            var path = Uri.UnescapeDataString(new Uri(url).AbsolutePath);
            int dot = path.LastIndexOf('.');
            int slash = path.LastIndexOf('/');
            return dot > slash ? path[(dot + 1)..] : "";
        }
        catch { return ""; }
    }

    private static string TitleOfUrl(string url)
    {
        try
        {
            var name = Uri.UnescapeDataString(new Uri(url).AbsolutePath);
            var idx = name.LastIndexOf('/') + 1;
            return name[idx..];
        }
        catch { return url; }
    }

     /// <summary>Pixel size "WxH" for jpeg/png/gif/webp by reading the image bytes (no OCR).</summary>
    private static string? ImageDimensions(byte[] img) =>
        DocumentTextExtractor.InspectImage(img) is { } f ? $"{f.Width}x{f.Height}" : null;
}

 /// <summary>A text chunk ready for the embedding (row of chunks.jsonl).</summary>
public sealed class RagChunk
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
     /// <summary>page = web page; document = site document (pdf/docx/csv/json); image = an image.</summary>
    public string SourceType { get; set; } = "page";
     /// <summary>Local path under files/ (when the raw copy is on).</summary>
    public string? FilePath { get; set; }
     /// <summary>Features of the document/image (pdf, docx, json, jpg...); null for pages.</summary>
    public string? DocumentType { get; set; }
    public List<string> HeadingPath { get; set; } = new();
    public string Text { get; set; } = "";
    public int Tokens { get; set; }
    public string TextHash { get; set; } = "";
    public string? DuplicateOf { get; set; }
    public DateTime CrawledAtUtc { get; set; }
}

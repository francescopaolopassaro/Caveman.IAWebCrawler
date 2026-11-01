# Caveman.IAWebCrawler

Web crawler for **.NET 10** that downloads a complete website and produces three output types (combinable):

| Mode (flags) | Output |
|---|---|
| `CrawlModes.Json` | **`json/results.json`** — all the extracted information: metadata, headings, paragraphs, links, images, tables, forms, JSON-LD, the Markdown body |
| `CrawlModes.Markdown` | **`markdown/*.md`** — one page per URL with front matter, `index.md` and a combined `site-<host>.md` (ready for AI/RAG pipelines) |
| `CrawlModes.Files` | **`files/<host>/...`** — a raw archive of **every** file of the site: html, js, css, images, fonts, **pdf, docx, csv, json, txt**, zip... with no metadata extraction |

## Installation (as a NuGet package)

```bash
dotnet pack src/Caveman.IAWebCrawler
dotnet add package Caveman.IAWebCrawler
```

## Usage

```csharp
using Caveman.IAWebCrawler;

var options = new CrawlerOptions
{
    StartUrl = "https://example.com/",
    Modes = CrawlModes.All,          // Json | Markdown | Files (combinable flags)
    OutputDirectory = "output",
    MaxPages = 200,
    MaxDepth = 5,                    // link depth (default 5; -1 = unlimited)
    DelayMilliseconds = 500,         // politeness toward the server

    // --- identity of the browser (anti-bot) ---
    BrowserIdentity = BrowserKind.ChromeWindows,  // 20 profiles available (see below)
    AcceptLanguage = AcceptLanguages.HeaderFor("en"), // 40 languages ready

    // --- optional session setup ---
    // Cookies = { ["token"] = "..." },           // initial cookies
    // ExtraHeaders = { ["X-Token"] = "..." },    // extra headers

    SaveRawResponse = modes.HasFlag(CrawlModes.Files),
    SaveAssetsForMarkdown = true,    // images copied into assets/ for the Markdown
    Log = msg => Console.WriteLine(msg)
};

using var crawler = new CavemanWebCrawler(options);
var result = await crawler.CrawlAsync();
```

An `HttpClient` provided by the caller is accepted too:

```csharp
var crawler = new CavemanWebCrawler(options, httpClient);
```

## Browser profiles (anti-bot)

`Browsers` offers **20 ready-to-use identities**: each one pairs a real User-Agent
with a coherent `Accept` header and the `sec-ch-ua` client-hint family, grouped by platform:

- **Windows**: Chrome (140 / 131-LT), Edge, Firefox, Brave
- **macOS**: Safari (18.6 / 17.6-LT), Chrome, Firefox, Edge
- **Linux**: Chrome, Edge, Firefox, Chromium
- **Android**: Chrome (phone/tablet), Firefox, Edge, Samsung Internet
- **iOS / iPadOS**: Safari (iPhone/iPad), Chrome, Edge, Firefox

`BrowserKind.None`/`Custom` uses the plain `CrawlerOptions.UserAgent`. Shorthand
aliases are accepted when parsing (`chrome`, `edge`, `firefox`, `safari`, `mobile`).

## Languages (Accept-Language)

`AcceptLanguages` builds the header exactly like a browser would do
(`it-IT,it;q=0.9,en;q=0.8`): locale, fallback language and English as the last net.
40 BCP-47 codes are in the catalogue: Italian, English (US/GB), German, French,
Spanish, Portuguese (PT/BR), Dutch, Russian, Polish, Ukrainian, Czech, Slovak,
Hungarian, Romanian, Bulgarian, Greek, Croatian, Slovenian, Serbian, Turkish,
Danish, Swedish, Norwegian, Finnish, Arabic, Hebrew, Persian, Hindi, Urdu, Bengali,
Chinese (simplified/traditional), Japanese, Korean, Thai, Vietnamese, Indonesian, Malay.

## Session warm-up

`SessionWarmUp` (default **on**) performs a GET on the start URL before the crawl:
session cookies, anti-bot tokens or consent cookies released at the first hit are
stored and automatically replayed on every following request (pages, assets and
documents). Rejected attempts (HTTP 403/429) are retried after a pause — the usual
behaviour of municipal CMS sites that release their token only after the first visit.

## Markdown output: self-contained snapshot

The Markdown output is a **complete snapshot for LLMs** — no references back to the
live site:

- body links point to the **local .md file** of the target page (original URL kept
  as link tooltip: `(... "source: https://…")`);
- images/point to **local copies** in `assets/` (or `files/` when the Files mode
  is enabled as well);
- inline base64 (data URI) images and inline SVG are kept as textual notes;
- teaser pages ("Read more…" bodies reached beyond the depth/page limits) are
  fetched at the end (`ExpandUncrawledLinks`, cap `MaxExpansionPages`) and listed
  in `index.md` under **Imported pages**, with the provenance in their front matter.

## RAG / LLM extras (`GenerateAiOutputs`)

- **`rag/chunks.jsonl`** — text chunks of pages, documents (pdf/docx/csv/json with
  text extracted via Stilo.Documents) and images (metadata + alt), with hash, token
  estimate and duplicates flag, `sourceType: page | document | image`.
- **`rag/pages.jsonl`** — one JSON row per page (url, title, description, headings).
- **`rag/corpus.txt`** — one-source-per-block text corpus for long prompts.
- **`rag/manifest.json`** — statistics and instructions for the embedding pipeline.
- **`llms.txt` / `llms-full.txt`** — the llmstxt.org index.

## Test console (test/Caveman.IAWebCrawler.Test)

```bash
dotnet run --project test/Caveman.IAWebCrawler.Test [url] [maxPages] [mode] [outputFolder] [depth]
              [--identity=...] [--language=...] [--cookie=name=value;...] [--header=Name=value;...]
```

- `mode`: `json | markdown | files | all` (default `all`)
- `identity`: one of the 20 browser profiles (default `chrome` = Chrome on Windows)
- `language`: one of the 40 catalog codes (default `it`) or a full header string

Example (municipal site used for the actual runs):

```bash
dotnet run --project test/Caveman.IAWebCrawler.Test https://www.comune.silea.tv.it/ 25 markdown output 5 --identity=chrome --language=it
```

## Key options

| Option | Default | Notes |
|---|---|---|
| `MaxDepth` | 5 | depth limit for pages only: assets/documents are always downloaded |
| `SessionWarmUp` | true | first-contact session tokens replayed on all requests |
| `DownloadAssets` / `DownloadDocuments` | true | assets and pdf/docx/csv/json/zip… downloaded |
| `DownloadExternalAssets` | true | CDN assets (icon fonts, shared css/js) downloaded for the local render |
| `SaveRawResponse` | true | raw copies in `files/` (Files mode) |
| `SaveAssetsForMarkdown` | true | image copies in `assets/` for the Markdown snapshot |
| `ExpandUncrawledLinks` | true | import the teaser pages in the Markdown snapshot (cap `MaxExpansionPages` = 100) |
| `FollowCssReferences` | true | recursive url()/@import extraction from the CSS (fonts, sprites, backgrounds) |
| `RespectRobotsTxt` | true | Disallow + Crawl-delay honoured |
| `BeautifyAssets` | true | de-minify archived js/css (readability) |
| `RagChunkSize` / `RagChunkOverlap` | 900 / 150 | chunking of the RAG corpus |

## Notes

- `LinkRewriter` post-processes the file archive so the downloaded site is browsable
  locally (href/src/srcset/action/url()/@import); references to unsaved resources stay absolute.
- Long paths are Windows-safe (MAX_PATH-proof) — segments are truncated and hashed when needed.

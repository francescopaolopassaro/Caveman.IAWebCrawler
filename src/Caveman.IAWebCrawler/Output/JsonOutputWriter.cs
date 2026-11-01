using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Caveman.IAWebCrawler.Output;

 /// <summary>Writes the full crawl result into a single indented JSON file.</summary>
public static class JsonOutputWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task WriteAsync(SiteCrawlResult result, string outputDirectory, CancellationToken ct = default)
    {
        var dir = Path.Combine(outputDirectory, "json");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "results.json");

        await using var fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, result, Options, ct);
        Console.WriteLine($"  → JSON: {path}");
    }
}

using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;

namespace corporate_dashboards.Services;

public interface ITextExtractor
{
    Task<List<(string SourceLabel, string Text)>> ExtractAsync(string fullPath, string fileName, string contentType, CancellationToken ct);
}

public sealed class TextExtractor : ITextExtractor
{
    public Task<List<(string SourceLabel, string Text)>> ExtractAsync(string fullPath, string fileName, string contentType, CancellationToken ct)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        if (ext == ".pdf" || contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase))
            return ExtractPdfAsync(fullPath, fileName, ct);

        if (ext == ".docx" || contentType.Contains("word", StringComparison.OrdinalIgnoreCase))
            return ExtractDocxAsync(fullPath, fileName, ct);

        if (ext == ".txt" || contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            return ExtractTxtAsync(fullPath, fileName, ct);

        return ExtractTxtAsync(fullPath, fileName, ct);
    }

    private static async Task<List<(string, string)>> ExtractTxtAsync(string path, string fileName, CancellationToken ct)
    {
        var txt = await File.ReadAllTextAsync(path, ct);
        return new() { (fileName, txt) };
    }

    private static Task<List<(string, string)>> ExtractPdfAsync(string path, string fileName, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var res = new List<(string, string)>();
            using var doc = PdfDocument.Open(path);

            for (int i = 1; i <= doc.NumberOfPages; i++)
            {
                ct.ThrowIfCancellationRequested();
                var page = doc.GetPage(i);
                var text = page.Text ?? "";
                if (!string.IsNullOrWhiteSpace(text))
                    res.Add(($"{fileName} p.{i}", text));
            }

            if (res.Count == 0) res.Add((fileName, ""));
            return res;
        }, ct);
    }

    private static Task<List<(string, string)>> ExtractDocxAsync(string path, string fileName, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            using var doc = WordprocessingDocument.Open(path, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            var text = body?.InnerText ?? "";
            return new List<(string, string)> { (fileName, text) };
        }, ct);
    }
}


using System.Text.Json;
using corporate_dashboards.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace corporate_dashboards.Services;

public sealed record RetrievedChunk(long ChunkId, long DocumentId, string FileName, string SourceLabel, string Text, double Similarity);

public interface IRetrievalService
{
    Task<List<RetrievedChunk>> RetrieveAsync(string question, string? embeddingModelOverride, CancellationToken ct);
    Task<string> AnswerAsync(string question, string? chatModelOverride, string? embeddingModelOverride, CancellationToken ct);
}

public sealed class RetrievalService : IRetrievalService
{
    private readonly AppDbContext _db;
    private readonly IOllamaClient _ollama;
    private readonly RagOptions _opt;

    public RetrievalService(AppDbContext db, IOllamaClient ollama, IOptions<RagOptions> opt)
    {
        _db = db;
        _ollama = ollama;
        _opt = opt.Value;
    }

    public async Task<List<RetrievedChunk>> RetrieveAsync(string question, string? embeddingModelOverride, CancellationToken ct)
    {
        var qEmb = await _ollama.EmbedAsync(question, embeddingModelOverride, ct);
        if (qEmb.Length == 0) return new();

        var chunks = await _db.Chunks
            .AsNoTracking()
            .Include(c => c.Document)
            .Where(c => c.Document != null && c.Document.Status == "Ready")
            .ToListAsync(ct);

        var scored = new List<RetrievedChunk>();

        foreach (var c in chunks)
        {
            ct.ThrowIfCancellationRequested();
            var emb = Deserialize(c.EmbeddingJson);
            if (emb.Length != qEmb.Length || emb.Length == 0) continue;

            var sim = Cosine(qEmb, emb);
            if (sim >= _opt.MinSimilarity)
            {
                scored.Add(new RetrievedChunk(
                    c.Id,
                    c.DocumentId,
                    c.Document!.FileName,
                    c.SourceLabel,
                    c.Text,
                    sim
                ));
            }
        }

        return scored
            .OrderByDescending(x => x.Similarity)
            .Take(Math.Max(1, _opt.TopK))
            .ToList();
    }

    public async Task<string> AnswerAsync(string question, string? chatModelOverride, string? embeddingModelOverride, CancellationToken ct)
    {
        var hits = await RetrieveAsync(question, embeddingModelOverride, ct);

        var context = hits.Count == 0
            ? "NO_CONTEXT"
            : string.Join("\n\n", hits.Select((h, i) =>
                $"[Source {i + 1}] {h.FileName} — {h.SourceLabel}\n{h.Text}"));

        var prompt =
            "You are a document Q&A assistant.\n" +
            "Rules:\n" +
            "- Use ONLY the provided sources to answer.\n" +
            "- If the sources do not contain the answer, say: \"Not found in the uploaded documents.\"\n" +
            "- When you use information, cite sources like [Source 1], [Source 2].\n" +
            "- Keep the answer factual and concise.\n\n" +
            "Question:\n" + question + "\n\n" +
            "Sources:\n" + context + "\n\n" +
            "Answer:\n";

        return await _ollama.ChatAsync(prompt, chatModelOverride, ct);
    }

    private static float[] Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<float[]>(json) ?? Array.Empty<float>(); }
        catch { return Array.Empty<float>(); }
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            var x = a[i];
            var y = b[i];
            dot += x * y;
            na += x * x;
            nb += y * y;
        }
        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom <= 0 ? 0 : dot / denom;
    }
}

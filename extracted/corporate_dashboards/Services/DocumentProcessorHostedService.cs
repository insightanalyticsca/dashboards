using System.Text.Json;
using corporate_dashboards.Data;
using corporate_dashboards.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace corporate_dashboards.Services;

public sealed class DocumentProcessorHostedService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly IDocumentQueue _q;
    private readonly ILogger<DocumentProcessorHostedService> _log;

    public DocumentProcessorHostedService(IServiceProvider sp, IDocumentQueue q, ILogger<DocumentProcessorHostedService> log)
    {
        _sp = sp;
        _q = q;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Document processor started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            long docId;
            try { docId = await _q.DequeueAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }

            try { await ProcessOneAsync(docId, stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "Processing failed for {DocId}", docId); }
        }
    }

    private async Task ProcessOneAsync(long docId, CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var extractor = scope.ServiceProvider.GetRequiredService<ITextExtractor>();
        var chunker = scope.ServiceProvider.GetRequiredService<IChunker>();
        var ollama = scope.ServiceProvider.GetRequiredService<IOllamaClient>();
        var ragOpt = scope.ServiceProvider.GetRequiredService<IOptions<RagOptions>>().Value;
        var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();

        var doc = await db.Documents.FirstOrDefaultAsync(x => x.Id == docId, ct);
        if (doc is null) return;

        doc.Status = "Processing";
        doc.ErrorMessage = null;
        await db.SaveChangesAsync(ct);

        try
        {
            var fullPath = Path.Combine(env.ContentRootPath, doc.StoredPath);
            var extracted = await extractor.ExtractAsync(fullPath, doc.FileName, doc.ContentType, ct);
            var chunks = chunker.Chunk(extracted);

            // clear old
            db.Chunks.RemoveRange(db.Chunks.Where(c => c.DocumentId == doc.Id));
            await db.SaveChangesAsync(ct);

            foreach (var (src, txt) in chunks)
            {
                ct.ThrowIfCancellationRequested();
                var embedText = txt.Length > ragOpt.MaxChunkChars ? txt.Substring(0, ragOpt.MaxChunkChars) : txt;
                var emb = await ollama.EmbedAsync(embedText, null, ct);
                db.Chunks.Add(new DocChunk
                {
                    DocumentId = doc.Id,
                    SourceLabel = src,
                    Text = txt,
                    EmbeddingJson = JsonSerializer.Serialize(emb)
                });
            }

            doc.Status = "Ready";
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            doc.Status = "Failed";
            doc.ErrorMessage = ex.Message;
            await db.SaveChangesAsync(ct);
            throw;
        }
    }
}

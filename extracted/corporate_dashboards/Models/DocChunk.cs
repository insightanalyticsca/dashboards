namespace corporate_dashboards.Models;

public sealed class DocChunk
{
    public long Id { get; set; }
    public long DocumentId { get; set; }

    public required string Text { get; set; }
    public required string SourceLabel { get; set; }

    // JSON float[] for SQLite portability
    public required string EmbeddingJson { get; set; }

    public Document? Document { get; set; }
}

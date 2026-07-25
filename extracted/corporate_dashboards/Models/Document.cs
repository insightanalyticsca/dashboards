using System.ComponentModel.DataAnnotations;

namespace corporate_dashboards.Models;

public sealed class Document
{
    public long Id { get; set; }

    [MaxLength(260)]
    public required string FileName { get; set; }

    [MaxLength(1024)]
    public required string StoredPath { get; set; }

    [MaxLength(200)]
    public required string ContentType { get; set; }

    public long SizeBytes { get; set; }
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;

    // Queued | Processing | Ready | Failed
    [MaxLength(40)]
    public required string Status { get; set; } = "Queued";

    public string? ErrorMessage { get; set; }

    public ICollection<DocChunk> Chunks { get; set; } = new List<DocChunk>();
}

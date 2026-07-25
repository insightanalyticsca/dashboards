using System.Threading.Channels;

namespace corporate_dashboards.Services;

public interface IDocumentQueue
{
    ValueTask EnqueueAsync(long documentId, CancellationToken ct);
    ValueTask<long> DequeueAsync(CancellationToken ct);
}

public sealed class DocumentQueue : IDocumentQueue
{
    private readonly Channel<long> _ch = Channel.CreateUnbounded<long>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask EnqueueAsync(long documentId, CancellationToken ct) => _ch.Writer.WriteAsync(documentId, ct);
    public ValueTask<long> DequeueAsync(CancellationToken ct) => _ch.Reader.ReadAsync(ct);
}

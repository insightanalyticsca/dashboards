using Microsoft.Extensions.Options;

namespace corporate_dashboards.Services;

public interface IChunker
{
    List<(string SourceLabel, string Text)> Chunk(List<(string SourceLabel, string Text)> extracted);
}

public sealed class Chunker : IChunker
{
    private readonly RagOptions _opt;
    public Chunker(IOptions<RagOptions> opt) => _opt = opt.Value;

    public List<(string SourceLabel, string Text)> Chunk(List<(string SourceLabel, string Text)> extracted)
    {
        var outChunks = new List<(string, string)>();

        foreach (var (src, raw) in extracted)
        {
            var text = Normalize(raw);
            if (string.IsNullOrWhiteSpace(text)) continue;

            int max = Math.Max(200, _opt.MaxChunkChars);
            int overlap = Math.Clamp(_opt.ChunkOverlapChars, 0, max - 1);

            int i = 0;
            int idx = 1;

            while (i < text.Length)
            {
                int len = Math.Min(max, text.Length - i);
                var chunk = text.Substring(i, len).Trim();
                if (!string.IsNullOrWhiteSpace(chunk))
                    outChunks.Add(($"{src} (chunk {idx++})", chunk));

                if (i + len >= text.Length) break;
                i += (len - overlap);
            }
        }

        return outChunks;
    }

    private static string Normalize(string s)
    {
        s = (s ?? "").Replace("\r", "");
        s = string.Join("\n", s.Split('\n').Select(x => x.TrimEnd()));
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Trim();
    }
}

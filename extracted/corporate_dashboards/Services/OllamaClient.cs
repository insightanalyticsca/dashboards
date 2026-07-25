using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace corporate_dashboards.Services;

public interface IOllamaClient
{
    Task<float[]> EmbedAsync(string text, string? embeddingModelOverride, CancellationToken ct);
    Task<string> ChatAsync(string prompt, string? chatModelOverride, CancellationToken ct);
}

public sealed class OllamaClient : IOllamaClient
{
    private readonly HttpClient _http;
    private readonly OllamaOptions _opt;

    public OllamaClient(HttpClient http, IOptions<OllamaOptions> opt)
    {
        _http = http;
        _opt = opt.Value;

        _http.BaseAddress = new Uri(_opt.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromMinutes(5);
    }

    public async Task<float[]> EmbedAsync(string text, string? embeddingModelOverride, CancellationToken ct)
    {
        var model = string.IsNullOrWhiteSpace(embeddingModelOverride)
            ? _opt.EmbeddingModel
            : embeddingModelOverride!.Trim();

        var payload = new { model, input = text };

        using var resp = await _http.PostAsJsonAsync("v1/embeddings", payload, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Ollama /v1/embeddings failed for model '{model}'. Status {(int)resp.StatusCode}. Body: {body}");
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        // OpenAI-style: { data: [ { embedding: [...] } ] }
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            throw new InvalidOperationException("Embeddings response missing 'data' array.");

        var first = data[0];
        if (!first.TryGetProperty("embedding", out var emb) || emb.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Embeddings response missing 'embedding' array.");

        var list = new List<float>(emb.GetArrayLength());
        foreach (var v in emb.EnumerateArray())
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out var f))
                list.Add(f);
        }

        if (list.Count == 0)
            throw new InvalidOperationException("Ollama returned an empty embedding array.");

        return list.ToArray();
    }

    public async Task<string> ChatAsync(string prompt, string? chatModelOverride, CancellationToken ct)
    {
        var model = string.IsNullOrWhiteSpace(chatModelOverride)
            ? _opt.ChatModel
            : chatModelOverride!.Trim();

        var payload = new { model, prompt, stream = false };

        using var resp = await _http.PostAsJsonAsync("api/generate", payload, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Ollama chat failed. Status {(int)resp.StatusCode}. Body: {body}");
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        return doc.RootElement.TryGetProperty("response", out var r)
            ? (r.GetString() ?? "")
            : "";
    }

    private async Task<HttpResponseMessage> PostWith404FallbackAsync<T>(
        string primaryPath,
        string fallbackPath,
        T payload,
        CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync(primaryPath, payload, ct);
        if (resp.IsSuccessStatusCode) return resp;

        if ((int)resp.StatusCode == 404)
        {
            resp.Dispose();
            var resp2 = await _http.PostAsJsonAsync(fallbackPath, payload, ct);
            if (resp2.IsSuccessStatusCode) return resp2;

            var body2 = await resp2.Content.ReadAsStringAsync(ct);
            resp2.Dispose();
            throw new HttpRequestException(
                $"Ollama embeddings endpoint not found (tried '{primaryPath}' then '{fallbackPath}'). " +
                $"Fallback status {(int)resp2.StatusCode}. Body: {body2}");
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        resp.Dispose();
        throw new HttpRequestException(
            $"Ollama embeddings failed at '{primaryPath}'. Status {(int)resp.StatusCode}. Body: {body}");
    }
}
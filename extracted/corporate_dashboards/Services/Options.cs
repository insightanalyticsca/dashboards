namespace corporate_dashboards.Services;

public sealed class StorageOptions
{
    public string UploadsRoot { get; set; } = "App_Data/uploads";
}

public sealed class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string ChatModel { get; set; } = "llama3.1";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
}

public sealed class RagOptions
{
    public int MaxChunkChars { get; set; } = 1800;
    public int ChunkOverlapChars { get; set; } = 200;
    public int TopK { get; set; } = 6;
    public double MinSimilarity { get; set; } = 0.25;
}

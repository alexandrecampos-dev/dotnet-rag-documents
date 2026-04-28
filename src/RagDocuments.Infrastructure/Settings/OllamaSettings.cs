namespace RagDocuments.Infrastructure.Settings;

public class OllamaSettings
{
    public const string SectionName = "Ollama";

    public string BaseUrl { get; init; } = "http://localhost:11434";
    public string EmbeddingModel { get; init; } = "nomic-embed-text";
    public string ChatModel { get; init; } = "llama3";
    public int EmbeddingDimensions { get; init; } = 768;
}

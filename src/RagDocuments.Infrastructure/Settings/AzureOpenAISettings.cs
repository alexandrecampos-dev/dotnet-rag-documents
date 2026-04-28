namespace RagDocuments.Infrastructure.Settings;

public class AzureOpenAISettings
{
    public const string SectionName = "AzureOpenAI";

    public string Endpoint { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public string EmbeddingDeployment { get; init; } = "text-embedding-ada-002";
    public string ChatDeployment { get; init; } = "gpt-4o";
    public int EmbeddingDimensions { get; init; } = 1536;
}

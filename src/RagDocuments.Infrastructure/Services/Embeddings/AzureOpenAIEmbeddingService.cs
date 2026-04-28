using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Options;
using OpenAI.Embeddings;
using RagDocuments.Application.Common.Interfaces;
using RagDocuments.Infrastructure.Settings;

namespace RagDocuments.Infrastructure.Services.Embeddings;

public class AzureOpenAIEmbeddingService : IEmbeddingService
{
    private readonly EmbeddingClient _client;
    private readonly AzureOpenAISettings _settings;

    public int Dimensions => _settings.EmbeddingDimensions;

    public AzureOpenAIEmbeddingService(IOptions<AzureOpenAISettings> options)
    {
        _settings = options.Value;

        var azureClient = new AzureOpenAIClient(
            new Uri(_settings.Endpoint),
            new AzureKeyCredential(_settings.ApiKey));

        _client = azureClient.GetEmbeddingClient(_settings.EmbeddingDeployment);
    }

    public async Task<float[]> GenerateAsync(string text, CancellationToken cancellationToken = default)
    {
        var response = await _client.GenerateEmbeddingAsync(text, cancellationToken: cancellationToken);
        return response.Value.ToFloats().ToArray();
    }

    public async Task<IReadOnlyList<float[]>> GenerateBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        var textList = texts.ToList();
        var response = await _client.GenerateEmbeddingsAsync(textList, cancellationToken: cancellationToken);
        return response.Value.Select(e => e.ToFloats().ToArray()).ToList();
    }
}

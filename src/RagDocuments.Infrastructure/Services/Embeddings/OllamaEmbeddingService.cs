using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RagDocuments.Application.Common.Interfaces;
using RagDocuments.Infrastructure.Settings;

namespace RagDocuments.Infrastructure.Services.Embeddings;

public class OllamaEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly OllamaSettings _settings;

    public int Dimensions => _settings.EmbeddingDimensions;

    public OllamaEmbeddingService(HttpClient httpClient, IOptions<OllamaSettings> options)
    {
        _httpClient = httpClient;
        _settings = options.Value;
        _httpClient.BaseAddress = new Uri(_settings.BaseUrl);
    }

    public async Task<float[]> GenerateAsync(string text, CancellationToken cancellationToken = default)
    {
        var request = new { model = _settings.EmbeddingModel, prompt = text };
        var response = await _httpClient.PostAsJsonAsync("/api/embeddings", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(cancellationToken: cancellationToken);
        return result?.Embedding ?? throw new InvalidOperationException("Empty embedding response from Ollama.");
    }

    public async Task<IReadOnlyList<float[]>> GenerateBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        var results = new List<float[]>();
        foreach (var text in texts)
        {
            results.Add(await GenerateAsync(text, cancellationToken));
        }
        return results;
    }

    private record OllamaEmbeddingResponse(
        [property: JsonPropertyName("embedding")] float[] Embedding);
}

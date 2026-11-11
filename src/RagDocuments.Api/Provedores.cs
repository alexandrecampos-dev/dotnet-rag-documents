using System.ClientModel;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;

namespace RagDocuments.Api;

/// <summary>
/// Um provedor de modelo. "Ollama" roda local; "OpenAICompativel" atende OpenAI, Azure OpenAI
/// (endpoint https://SEU-RECURSO.openai.azure.com/openai/v1/) e Groq (https://api.groq.com/openai/v1),
/// porque todos falam a mesma API.
/// </summary>
internal sealed class OpcoesDeProvedor
{
    public string Tipo { get; set; } = "Ollama";
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string? ChaveDeApi { get; set; }
    public string Modelo { get; set; } = string.Empty;
}

/// <summary>
/// A aplicação só conhece duas operações: gerar (IChatClient) e embutir (IEmbeddingGenerator).
/// Qual implementação entra é decisão de configuração. Chat e embedding são independentes:
/// dá para gerar com Groq e embutir com Ollama, por exemplo (Groq não oferece embeddings).
/// </summary>
internal static class Provedores
{
    public const string FonteDeTelemetria = "RagDocuments.IA";

    public static IServiceCollection AddProvedoresDeIa(this IServiceCollection services, IConfiguration configuracao)
    {
        var chat = configuracao.GetSection("Ia:Chat").Get<OpcoesDeProvedor>()
            ?? throw new InvalidOperationException("Configure a seção Ia:Chat.");
        var embedding = configuracao.GetSection("Ia:Embedding").Get<OpcoesDeProvedor>()
            ?? throw new InvalidOperationException("Configure a seção Ia:Embedding.");

        services.AddChatClient(_ => CriarChat(chat))
            .UseOpenTelemetry(sourceName: FonteDeTelemetria, configure: o => o.EnableSensitiveData = false)
            .UseLogging();

        services.AddEmbeddingGenerator(_ => CriarEmbedding(embedding))
            .UseOpenTelemetry(sourceName: FonteDeTelemetria)
            .UseLogging();

        return services;
    }

    private static IChatClient CriarChat(OpcoesDeProvedor o) => o.Tipo switch
    {
        "Ollama" => new OllamaApiClient(new Uri(o.Endpoint), o.Modelo),
        "OpenAICompativel" => ClienteOpenAI(o).GetChatClient(o.Modelo).AsIChatClient(),
        _ => throw new InvalidOperationException($"Provedor de chat desconhecido: '{o.Tipo}'. Use Ollama ou OpenAICompativel."),
    };

    private static IEmbeddingGenerator<string, Embedding<float>> CriarEmbedding(OpcoesDeProvedor o) => o.Tipo switch
    {
        "Ollama" => new OllamaApiClient(new Uri(o.Endpoint), o.Modelo),
        "OpenAICompativel" => ClienteOpenAI(o).GetEmbeddingClient(o.Modelo).AsIEmbeddingGenerator(),
        _ => throw new InvalidOperationException($"Provedor de embedding desconhecido: '{o.Tipo}'. Use Ollama ou OpenAICompativel."),
    };

    private static OpenAIClient ClienteOpenAI(OpcoesDeProvedor o)
    {
        if (string.IsNullOrWhiteSpace(o.ChaveDeApi))
            throw new InvalidOperationException("O provedor OpenAICompativel exige ChaveDeApi (use variável de ambiente ou user-secrets).");
        return new OpenAIClient(new ApiKeyCredential(o.ChaveDeApi), new OpenAIClientOptions { Endpoint = new Uri(o.Endpoint) });
    }
}

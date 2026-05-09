using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RagDocuments.Application.Common.Interfaces;
using RagDocuments.Infrastructure.Persistence;
using RagDocuments.Infrastructure.Services;
using RagDocuments.Infrastructure.Services.Embeddings;
using RagDocuments.Infrastructure.Services.Llm;
using RagDocuments.Infrastructure.Settings;

namespace RagDocuments.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AzureOpenAISettings>(
            configuration.GetSection(AzureOpenAISettings.SectionName));

        services.Configure<OllamaSettings>(
            configuration.GetSection(OllamaSettings.SectionName));

        services.Configure<ChunkingSettings>(
            configuration.GetSection(ChunkingSettings.SectionName));

        var provider = configuration.GetValue<string>("AI:EmbeddingProvider") ?? "AzureOpenAI";

        if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            var dimensions = configuration.GetValue<int>("Ollama:EmbeddingDimensions", 768);
            services.Configure<VectorDimensionOptions>(o => o.Dimensions = dimensions);
            services.AddHttpClient<IEmbeddingService, OllamaEmbeddingService>();
            services.AddHttpClient<ILlmService, OllamaLlmService>();
        }
        else
        {
            var dimensions = configuration.GetValue<int>("AzureOpenAI:EmbeddingDimensions", 1536);
            services.Configure<VectorDimensionOptions>(o => o.Dimensions = dimensions);
            services.AddScoped<IEmbeddingService, AzureOpenAIEmbeddingService>();
            services.AddScoped<ILlmService, AzureOpenAILlmService>();
        }

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("DefaultConnection"),
                npgsql => npgsql.UseVector()));

        services.AddScoped<IApplicationDbContext>(sp =>
            sp.GetRequiredService<ApplicationDbContext>());

        services.AddScoped<IVectorSearchService, VectorSearchService>();
        services.AddScoped<ITextChunker, TextChunker>();

        return services;
    }
}

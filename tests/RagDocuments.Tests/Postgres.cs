using Npgsql;
using RagDocuments.Core;
using RagDocuments.Core.Armazenamento;
using RagDocuments.Core.Ingestao;
using RagDocuments.Core.Respostas;
using Testcontainers.PostgreSql;

namespace RagDocuments.Tests;

/// <summary>Um PostgreSQL com pgvector de verdade, em container, compartilhado pelos testes de integração.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Banco vazio e exclusivo para um teste.</summary>
    public async Task<string> NovoBancoAsync()
    {
        var nome = $"t{Guid.NewGuid():N}";
        await using (var conexao = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await conexao.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {nome}", conexao);
            await cmd.ExecuteNonQueryAsync();
        }
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = nome }.ConnectionString;
    }
}

[CollectionDefinition(Nome)]
public sealed class ColecaoPostgres : ICollectionFixture<PostgresFixture>
{
    public const string Nome = "postgres";
}

/// <summary>O pipeline montado como na aplicação, com embedding e chat de teste.</summary>
public sealed class Pipeline : IAsyncDisposable
{
    private Pipeline(NpgsqlDataSource fonte, OpcoesRag opcoes, EmbeddingDeTeste embedding, ChatDeTeste chat)
    {
        Fonte = fonte;
        Opcoes = opcoes;
        Embedding = embedding;
        Chat = chat;
        Banco = new BancoVetorial(fonte, opcoes);
        Ingestao = new IngestaoService(Banco, embedding, opcoes);
        Respostas = new RespostaService(Banco, embedding, chat, opcoes);
    }

    public NpgsqlDataSource Fonte { get; }
    public OpcoesRag Opcoes { get; }
    public EmbeddingDeTeste Embedding { get; }
    public ChatDeTeste Chat { get; }
    public BancoVetorial Banco { get; }
    public IngestaoService Ingestao { get; }
    public RespostaService Respostas { get; }

    public static async Task<Pipeline> CriarAsync(string conexao, Action<OpcoesRag>? ajustar = null)
    {
        // Limiar calibrado para o embedding de teste (saco de palavras), que tem outra escala que um modelo real.
        var opcoes = new OpcoesRag { ModeloDeEmbedding = "teste-bow", DimensoesDoEmbedding = 256, TamanhoDoTrecho = 600, Sobreposicao = 120, SimilaridadeMinima = 0.3 };
        ajustar?.Invoke(opcoes);
        var construtor = new NpgsqlDataSourceBuilder(conexao);
        construtor.UseVector();
        var pipeline = new Pipeline(construtor.Build(), opcoes, new EmbeddingDeTeste(opcoes.DimensoesDoEmbedding), new ChatDeTeste());
        await pipeline.Banco.PrepararAsync();
        return pipeline;
    }

    public async Task IngerirExemplosAsync()
    {
        foreach (var arquivo in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "exemplos"), "*.md").Order())
            await Ingestao.IngerirAsync(Path.GetFileName(arquivo), await File.ReadAllTextAsync(arquivo), arquivo, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await Fonte.DisposeAsync();
}

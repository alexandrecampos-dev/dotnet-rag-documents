using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using RagDocuments.Core;
using static RagDocuments.Tests.Contexto;

namespace RagDocuments.Tests;

/// <summary>A API de ponta a ponta, com PostgreSQL real e os modelos trocados pelos de teste.</summary>
[Collection(ColecaoPostgres.Nome)]
public sealed class ApiTests(PostgresFixture postgres)
{
    private sealed class Api(string conexao) : WebApplicationFactory<Program>
    {
        public ChatDeTeste Chat { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Rag", conexao);
            builder.UseSetting("Rag:ModeloDeEmbedding", "teste-bow");
            builder.UseSetting("Rag:DimensoesDoEmbedding", "256");
            builder.UseSetting("Rag:SimilaridadeMinima", "0.3");
            builder.UseSetting("Rag:PrefixoDaPergunta", "");
            builder.UseSetting("Rag:PrefixoDoDocumento", "");
            builder.ConfigureTestServices(s =>
            {
                s.AddSingleton<IChatClient>(Chat);
                s.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new EmbeddingDeTeste(256));
            });
        }
    }

    [Fact]
    public async Task Ingere_e_responde_com_fonte()
    {
        await using var api = new Api(await postgres.NovoBancoAsync());
        using var cliente = api.CreateClient();

        var ingestao = await cliente.PostAsJsonAsync("/documentos",
            new { nome = "ferias.md", conteudo = "# Fracionamento\n\nAs férias podem ser divididas em até três períodos." }, Ct);
        ingestao.EnsureSuccessStatusCode();

        var resposta = await cliente.PostAsJsonAsync("/perguntas", new { pergunta = "Em quantos períodos as férias podem ser divididas?" }, Ct);
        var corpo = await resposta.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Contains("\"situacao\":\"respondida\"", corpo, StringComparison.Ordinal);
        Assert.Contains("ferias.md", corpo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Corpo_invalido_responde_400_e_nao_500()
    {
        await using var api = new Api(await postgres.NovoBancoAsync());
        using var cliente = api.CreateClient();
        // "férias" em Latin-1: bytes que não são UTF-8 válido.
        var conteudo = new ByteArrayContent(Encoding.Latin1.GetBytes("{\"pergunta\":\"Quantos dias de férias?\"}"));
        conteudo.Headers.ContentType = new("application/json");

        var resposta = await cliente.PostAsync("/perguntas", conteudo, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task Pergunta_vazia_responde_400()
    {
        await using var api = new Api(await postgres.NovoBancoAsync());
        using var cliente = api.CreateClient();

        var resposta = await cliente.PostAsJsonAsync("/perguntas", new { pergunta = "  " }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal(0, api.Chat.Chamadas);
    }
}

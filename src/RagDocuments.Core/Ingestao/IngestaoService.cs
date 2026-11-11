using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using RagDocuments.Core.Armazenamento;

namespace RagDocuments.Core.Ingestao;

public sealed record ResultadoDaIngestao(Guid DocumentoId, int Trechos, bool JaEstavaAtualizado, bool Substituiu);

public sealed class IngestaoService(
    BancoVetorial banco,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    OpcoesRag opcoes)
{
    private const int LoteDeEmbeddings = 32;

    /// <summary>
    /// Mesmo nome e mesmo conteúdo: não faz nada (nem gasta embedding). Mesmo nome e conteúdo novo:
    /// substitui o documento inteiro numa transação, sem deixar trecho velho misturado com o novo.
    /// </summary>
    public async Task<ResultadoDaIngestao> IngerirAsync(string nome, string conteudo, string? origem, CancellationToken ct = default)
    {
        using var atividade = Telemetria.Fonte.StartActivity("rag.ingestao");
        nome = nome.Trim();
        if (nome.Length == 0)
            throw new ArgumentException("Informe o nome do documento.", nameof(nome));
        if (string.IsNullOrWhiteSpace(conteudo))
            throw new ArgumentException("O documento está vazio.", nameof(conteudo));

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conteudo.Trim())));
        var existente = await banco.BuscarDocumentoPorNomeAsync(nome, ct);
        if (existente is { } e && e.Hash == hash)
        {
            atividade?.SetTag("rag.ingestao.ja_atualizado", true);
            var trechosAtuais = (await banco.ListarAsync(ct)).First(d => d.Id == e.Id).Trechos;
            return new ResultadoDaIngestao(e.Id, trechosAtuais, JaEstavaAtualizado: true, Substituiu: false);
        }

        var trechos = Fatiador.Fatiar(conteudo, opcoes.TamanhoDoTrecho, opcoes.Sobreposicao);
        var vetores = new List<ReadOnlyMemory<float>>(trechos.Count);
        foreach (var lote in trechos.Chunk(LoteDeEmbeddings))
        {
            var gerados = await embeddings.GenerateAsync(
                lote.Select(t => opcoes.PrefixoDoDocumento + t.TextoParaEmbedding), cancellationToken: ct);
            foreach (var g in gerados)
            {
                if (g.Vector.Length != opcoes.DimensoesDoEmbedding)
                {
                    throw new InvalidOperationException(
                        $"O modelo devolveu vetores de {g.Vector.Length} dimensões, mas Rag:DimensoesDoEmbedding é " +
                        $"{opcoes.DimensoesDoEmbedding}. Ajuste a configuração para o modelo em uso.");
                }
                vetores.Add(g.Vector);
            }
        }

        var id = await banco.GravarAsync(nome, origem, hash, trechos, vetores, ct);
        atividade?.SetTag("rag.ingestao.trechos", trechos.Count);
        return new ResultadoDaIngestao(id, trechos.Count, JaEstavaAtualizado: false, Substituiu: existente is not null);
    }
}

using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Pgvector;
using RagDocuments.Core.Ingestao;

namespace RagDocuments.Core.Armazenamento;

public sealed record DocumentoResumo(Guid Id, string Nome, string? Origem, int Trechos, DateTimeOffset IngeridoEm);

/// <param name="Similaridade">Cosseno entre a pergunta e o trecho (1 = idêntico), ou null se veio só da busca textual.</param>
/// <param name="CoberturaTextual">Fração dos termos da pergunta (já sem palavras vazias e reduzidos ao radical) presentes no trecho.</param>
public sealed record TrechoEncontrado(
    Guid TrechoId, Guid DocumentoId, string Documento, string? Secao, string Conteudo,
    double Pontuacao, double? Similaridade, double CoberturaTextual);

public sealed class ModeloDeEmbeddingDivergenteException(string mensagem) : Exception(mensagem);

/// <summary>
/// PostgreSQL + pgvector com SQL explícito. A dimensão do vetor vem da configuração, e o modelo de
/// embedding usado na indexação fica gravado: trocar de modelo sem reindexar é recusado na subida,
/// em vez de produzir buscas silenciosamente erradas.
/// </summary>
public sealed class BancoVetorial(NpgsqlDataSource fonte, OpcoesRag opcoes)
{
    public async Task PrepararAsync(CancellationToken ct = default)
    {
        var dimensoes = opcoes.DimensoesDoEmbedding.ToString(CultureInfo.InvariantCulture);
        var idioma = opcoes.IdiomaDaBuscaTextual;
        if (!idioma.All(char.IsAsciiLetterLower))
            throw new InvalidOperationException($"Idioma de busca textual inválido: '{idioma}'.");

        await using (var conexao = await fonte.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector", conexao))
            await cmd.ExecuteNonQueryAsync(ct);

        // A extensão recém-criada precisa ser recarregada pelos tipos do Npgsql.
        await using (var conexao = await fonte.OpenConnectionAsync(ct))
        {
            await conexao.ReloadTypesAsync(ct);
            await Executar(conexao, $"""
                CREATE TABLE IF NOT EXISTS rag_configuracao (
                    chave text PRIMARY KEY,
                    valor text NOT NULL);

                CREATE TABLE IF NOT EXISTS documentos (
                    id uuid PRIMARY KEY,
                    nome text NOT NULL UNIQUE,
                    origem text,
                    hash_conteudo text NOT NULL,
                    ingerido_em timestamptz NOT NULL DEFAULT now());

                CREATE TABLE IF NOT EXISTS trechos (
                    id uuid PRIMARY KEY,
                    documento_id uuid NOT NULL REFERENCES documentos(id) ON DELETE CASCADE,
                    ordem int NOT NULL,
                    secao text,
                    conteudo text NOT NULL,
                    embedding vector({dimensoes}) NOT NULL,
                    busca tsvector GENERATED ALWAYS AS
                        (to_tsvector('{idioma}', coalesce(secao, '') || ' ' || conteudo)) STORED);

                CREATE INDEX IF NOT EXISTS trechos_embedding_hnsw ON trechos USING hnsw (embedding vector_cosine_ops);
                CREATE INDEX IF NOT EXISTS trechos_busca_gin ON trechos USING gin (busca);
                CREATE INDEX IF NOT EXISTS trechos_documento ON trechos (documento_id);
                """, ct);

            await VerificarModeloAsync(conexao, ct);
        }
    }

    private async Task VerificarModeloAsync(NpgsqlConnection conexao, CancellationToken ct)
    {
        // O prefixo do documento também muda os vetores gravados, então faz parte da assinatura.
        var esperado = $"{opcoes.ModeloDeEmbedding}|{opcoes.DimensoesDoEmbedding}|{opcoes.PrefixoDoDocumento.Trim()}";
        await using var ler = new NpgsqlCommand("SELECT valor FROM rag_configuracao WHERE chave = 'embedding'", conexao);
        var gravado = (string?)await ler.ExecuteScalarAsync(ct);

        if (gravado is null)
        {
            await using var gravar = new NpgsqlCommand(
                "INSERT INTO rag_configuracao (chave, valor) VALUES ('embedding', @v) ON CONFLICT (chave) DO NOTHING", conexao);
            gravar.Parameters.AddWithValue("v", esperado);
            await gravar.ExecuteNonQueryAsync(ct);
            return;
        }

        if (gravado != esperado)
        {
            throw new ModeloDeEmbeddingDivergenteException(
                $"O banco foi indexado com o embedding '{gravado.Replace('|', ' ')}' e a configuração atual é " +
                $"'{esperado.Replace('|', ' ')}'. Vetores de modelos diferentes não são comparáveis: use o mesmo " +
                "modelo ou crie um banco novo e reingira os documentos.");
        }
    }

    public async Task<(Guid Id, string Hash)?> BuscarDocumentoPorNomeAsync(string nome, CancellationToken ct = default)
    {
        await using var cmd = fonte.CreateCommand("SELECT id, hash_conteudo FROM documentos WHERE nome = @nome");
        cmd.Parameters.AddWithValue("nome", nome);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetGuid(0), r.GetString(1)) : null;
    }

    /// <summary>Grava o documento e todos os trechos numa transação; se já existia com esse nome, substitui.</summary>
    public async Task<Guid> GravarAsync(
        string nome, string? origem, string hash, IReadOnlyList<Trecho> trechos,
        IReadOnlyList<ReadOnlyMemory<float>> embeddings, CancellationToken ct = default)
    {
        await using var conexao = await fonte.OpenConnectionAsync(ct);
        await using var tx = await conexao.BeginTransactionAsync(ct);

        await using (var apagar = new NpgsqlCommand("DELETE FROM documentos WHERE nome = @nome", conexao, tx))
        {
            apagar.Parameters.AddWithValue("nome", nome);
            await apagar.ExecuteNonQueryAsync(ct);
        }

        var id = Guid.CreateVersion7();
        await using (var inserir = new NpgsqlCommand(
            "INSERT INTO documentos (id, nome, origem, hash_conteudo) VALUES (@id, @nome, @origem, @hash)", conexao, tx))
        {
            inserir.Parameters.AddWithValue("id", id);
            inserir.Parameters.AddWithValue("nome", nome);
            inserir.Parameters.AddWithValue("origem", (object?)origem ?? DBNull.Value);
            inserir.Parameters.AddWithValue("hash", hash);
            await inserir.ExecuteNonQueryAsync(ct);
        }

        await using (var lote = new NpgsqlBatch(conexao, tx))
        {
            for (var i = 0; i < trechos.Count; i++)
            {
                var c = new NpgsqlBatchCommand(
                    "INSERT INTO trechos (id, documento_id, ordem, secao, conteudo, embedding) VALUES ($1, $2, $3, $4, $5, $6)");
                c.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
                c.Parameters.Add(new NpgsqlParameter { Value = id });
                c.Parameters.Add(new NpgsqlParameter { Value = trechos[i].Ordem });
                c.Parameters.Add(new NpgsqlParameter { Value = (object?)trechos[i].Secao ?? DBNull.Value });
                c.Parameters.Add(new NpgsqlParameter { Value = trechos[i].Conteudo });
                c.Parameters.Add(new NpgsqlParameter { Value = new Vector(embeddings[i]) });
                lote.BatchCommands.Add(c);
            }
            if (lote.BatchCommands.Count > 0)
                await lote.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<IReadOnlyList<DocumentoResumo>> ListarAsync(CancellationToken ct = default)
    {
        await using var cmd = fonte.CreateCommand("""
            SELECT d.id, d.nome, d.origem, count(t.id)::int, d.ingerido_em
            FROM documentos d LEFT JOIN trechos t ON t.documento_id = d.id
            GROUP BY d.id ORDER BY d.nome
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var lista = new List<DocumentoResumo>();
        while (await r.ReadAsync(ct))
            lista.Add(new DocumentoResumo(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt32(3), r.GetFieldValue<DateTimeOffset>(4)));
        return lista;
    }

    public async Task<bool> ApagarAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = fonte.CreateCommand("DELETE FROM documentos WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// Busca híbrida numa consulta só: os N mais próximos por cosseno (índice HNSW) e os N melhores pela
    /// busca textual do PostgreSQL (índice GIN), fundidos por Reciprocal Rank Fusion (k = 60).
    /// A vetorial acha pelo significado; a textual acha nome próprio, sigla e código que o embedding borra.
    /// A textual recupera por qualquer termo da pergunta (OU) e devolve a cobertura, para o filtro de
    /// relevância decidir; exigir todos os termos (E) perde quase toda pergunta em linguagem natural.
    /// </summary>
    public async Task<IReadOnlyList<TrechoEncontrado>> BuscarAsync(
        ReadOnlyMemory<float> vetorDaPergunta, string pergunta, CancellationToken ct = default)
    {
        var idioma = opcoes.IdiomaDaBuscaTextual;
        await using var cmd = fonte.CreateCommand($"""
            WITH vetorial AS (
                SELECT id, row_number() OVER (ORDER BY embedding <=> @vetor) AS posicao,
                       1 - (embedding <=> @vetor) AS similaridade
                FROM trechos
                ORDER BY embedding <=> @vetor
                LIMIT @candidatos
            ),
            consulta AS (
                SELECT replace(plainto_tsquery('{idioma}', @pergunta)::text, ' & ', ' | ')::tsquery AS q,
                       tsvector_to_array(to_tsvector('{idioma}', @pergunta)) AS termos
            ),
            textual AS (
                SELECT t.id,
                       row_number() OVER (ORDER BY ts_rank_cd(t.busca, c.q) DESC) AS posicao,
                       (SELECT count(*) FROM unnest(c.termos) termo WHERE t.busca @@ quote_literal(termo)::tsquery)::float8
                           / greatest(cardinality(c.termos), 1) AS cobertura
                FROM trechos t, consulta c
                WHERE cardinality(c.termos) > 0 AND t.busca @@ c.q
                ORDER BY ts_rank_cd(t.busca, c.q) DESC
                LIMIT @candidatos
            )
            SELECT t.id, t.documento_id, d.nome, t.secao, t.conteudo,
                   coalesce(1.0 / (60 + v.posicao), 0) + coalesce(1.0 / (60 + x.posicao), 0) AS pontuacao,
                   v.similaridade,
                   coalesce(x.cobertura, 0) AS cobertura
            FROM trechos t
            JOIN documentos d ON d.id = t.documento_id
            LEFT JOIN vetorial v ON v.id = t.id
            LEFT JOIN textual x ON x.id = t.id
            WHERE v.id IS NOT NULL OR x.id IS NOT NULL
            ORDER BY pontuacao DESC
            LIMIT @limite
            """);
        cmd.Parameters.AddWithValue("vetor", new Vector(vetorDaPergunta));
        cmd.Parameters.Add(new NpgsqlParameter("pergunta", NpgsqlDbType.Text) { Value = pergunta });
        cmd.Parameters.AddWithValue("candidatos", opcoes.CandidatosPorBusca);
        cmd.Parameters.AddWithValue("limite", opcoes.CandidatosPorBusca);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        var lista = new List<TrechoEncontrado>();
        while (await r.ReadAsync(ct))
        {
            lista.Add(new TrechoEncontrado(
                r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4),
                Convert.ToDouble(r.GetValue(5), CultureInfo.InvariantCulture),
                r.IsDBNull(6) ? null : r.GetDouble(6),
                r.GetDouble(7)));
        }
        return lista;
    }

    private static async Task Executar(NpgsqlConnection conexao, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conexao);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

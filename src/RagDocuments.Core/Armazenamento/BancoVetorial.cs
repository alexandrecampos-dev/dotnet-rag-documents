using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Pgvector;
using RagDocuments.Core.Ingestao;

namespace RagDocuments.Core.Armazenamento;

public sealed record DocumentoResumo(Guid Id, string Nome, string? Origem, int Trechos, DateTimeOffset IngeridoEm);

public sealed record DocumentoGravado(Guid Id, string Hash, int Trechos);

/// <param name="Similaridade">Cosseno entre a pergunta e o trecho (1 = idêntico), ou null se veio só da busca textual.</param>
/// <param name="CoberturaTextual">Fração dos termos da pergunta (já sem palavras vazias e reduzidos ao radical) presentes no trecho.</param>
public sealed record TrechoEncontrado(
    Guid TrechoId, Guid DocumentoId, string Documento, string? Secao, string Conteudo,
    double Pontuacao, double? Similaridade, double CoberturaTextual);

public sealed class ModeloDeEmbeddingDivergenteException(string mensagem) : Exception(mensagem);

/// <summary>
/// PostgreSQL + pgvector com SQL explícito. A dimensão do vetor vem da configuração, e o que define
/// o índice (modelo de embedding, dimensões, prefixo e idioma da busca textual) fica gravado: subir
/// com outra configuração sem reindexar é recusado, em vez de produzir buscas silenciosamente erradas.
/// </summary>
public sealed class BancoVetorial(NpgsqlDataSource fonte, OpcoesRag opcoes)
{
    public async Task PrepararAsync(CancellationToken ct = default)
    {
        opcoes.Validar();
        var dimensoes = opcoes.DimensoesDoEmbedding.ToString(CultureInfo.InvariantCulture);
        var idioma = opcoes.IdiomaDaBuscaTextual;

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

            await VerificarAssinaturaAsync(conexao, ct);
        }
    }

    private async Task VerificarAssinaturaAsync(NpgsqlConnection conexao, CancellationToken ct)
    {
        // Tudo o que muda o conteúdo do índice: os vetores (modelo, dimensões, prefixo) e a coluna de busca textual (idioma).
        var esperado = string.Join('|',
            opcoes.ModeloDeEmbedding, opcoes.DimensoesDoEmbedding.ToString(CultureInfo.InvariantCulture),
            opcoes.PrefixoDoDocumento.Trim(), opcoes.IdiomaDaBuscaTextual);

        await using (var gravar = new NpgsqlCommand(
            "INSERT INTO rag_configuracao (chave, valor) VALUES ('indice', @v) ON CONFLICT (chave) DO NOTHING", conexao))
        {
            gravar.Parameters.AddWithValue("v", esperado);
            await gravar.ExecuteNonQueryAsync(ct);
        }

        // Relê depois de gravar: se duas instâncias subirem juntas com configurações diferentes, a segunda percebe.
        await using var ler = new NpgsqlCommand("SELECT valor FROM rag_configuracao WHERE chave = 'indice'", conexao);
        var gravado = (string)(await ler.ExecuteScalarAsync(ct))!;
        if (gravado != esperado)
        {
            throw new ModeloDeEmbeddingDivergenteException(
                $"O banco foi indexado com '{gravado.Replace('|', ' ')}' (modelo, dimensões, prefixo, idioma) e a " +
                $"configuração atual é '{esperado.Replace('|', ' ')}'. Índices de configurações diferentes não são " +
                "comparáveis: volte à configuração anterior ou crie um banco novo e reingira os documentos.");
        }
    }

    public async Task<DocumentoGravado?> BuscarDocumentoPorNomeAsync(string nome, CancellationToken ct = default)
    {
        await using var cmd = fonte.CreateCommand("""
            SELECT d.id, d.hash_conteudo, (SELECT count(*)::int FROM trechos t WHERE t.documento_id = d.id)
            FROM documentos d WHERE d.nome = @nome
            """);
        cmd.Parameters.AddWithValue("nome", nome);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new DocumentoGravado(r.GetGuid(0), r.GetString(1), r.GetInt32(2)) : null;
    }

    /// <summary>
    /// Grava o documento e todos os trechos numa transação; se já existia com esse nome, substitui.
    /// Uma trava por nome serializa ingestões simultâneas do mesmo documento, e o hash é conferido de
    /// novo dentro dela: quem chegar depois com o mesmo conteúdo não grava nada.
    /// </summary>
    public async Task<(DocumentoGravado Documento, bool Gravou)> GravarAsync(
        string nome, string? origem, string hash, IReadOnlyList<Trecho> trechos,
        IReadOnlyList<ReadOnlyMemory<float>> embeddings, CancellationToken ct = default)
    {
        await using var conexao = await fonte.OpenConnectionAsync(ct);
        await using var tx = await conexao.BeginTransactionAsync(ct);

        await using (var travar = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@nome, 0))", conexao, tx))
        {
            travar.Parameters.AddWithValue("nome", nome);
            await travar.ExecuteNonQueryAsync(ct);
        }

        await using (var conferir = new NpgsqlCommand("""
            SELECT d.id, (SELECT count(*)::int FROM trechos t WHERE t.documento_id = d.id)
            FROM documentos d WHERE d.nome = @nome AND d.hash_conteudo = @hash
            """, conexao, tx))
        {
            conferir.Parameters.AddWithValue("nome", nome);
            conferir.Parameters.AddWithValue("hash", hash);
            await using var r = await conferir.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
                return (new DocumentoGravado(r.GetGuid(0), hash, r.GetInt32(1)), false);
        }

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
        return (new DocumentoGravado(id, hash, trechos.Count), true);
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
    /// Busca híbrida numa consulta só. Cada lado limita os próprios candidatos usando o índice (HNSW para
    /// o cosseno, GIN para o texto); só os ids desses candidatos são unidos e cruzados com as tabelas pela
    /// chave primária. A fusão é Reciprocal Rank Fusion (k = 60).
    /// A textual recupera por qualquer termo da pergunta (OU) e devolve a cobertura, calculada por
    /// interseção de arrays de radicais, sem montar tsquery a partir de texto.
    /// </summary>
    internal string SqlDaBusca() => $"""
        WITH vetorial AS (
            SELECT id, similaridade, row_number() OVER (ORDER BY distancia) AS posicao
            FROM (
                SELECT id, embedding <=> @vetor AS distancia, 1 - (embedding <=> @vetor) AS similaridade
                FROM trechos
                ORDER BY embedding <=> @vetor
                LIMIT @candidatos
            ) proximos
        ),
        consulta AS (
            SELECT replace(plainto_tsquery('{opcoes.IdiomaDaBuscaTextual}', @pergunta)::text, ' & ', ' | ')::tsquery AS q,
                   tsvector_to_array(to_tsvector('{opcoes.IdiomaDaBuscaTextual}', @pergunta)) AS termos
        ),
        textual AS (
            SELECT id, cobertura, row_number() OVER (ORDER BY relevancia DESC) AS posicao
            FROM (
                SELECT t.id, ts_rank_cd(t.busca, c.q) AS relevancia,
                       cardinality(ARRAY(SELECT unnest(c.termos) INTERSECT SELECT unnest(tsvector_to_array(t.busca))))::float8
                           / greatest(cardinality(c.termos), 1) AS cobertura
                FROM trechos t, consulta c
                WHERE cardinality(c.termos) > 0 AND t.busca @@ c.q
                ORDER BY relevancia DESC
                LIMIT @candidatos
            ) melhores
        ),
        candidatos AS (SELECT id FROM vetorial UNION SELECT id FROM textual)
        SELECT t.id, t.documento_id, d.nome, t.secao, t.conteudo,
               coalesce(1.0 / (60 + v.posicao), 0) + coalesce(1.0 / (60 + x.posicao), 0) AS pontuacao,
               v.similaridade,
               coalesce(x.cobertura, 0) AS cobertura
        FROM candidatos k
        JOIN trechos t ON t.id = k.id
        JOIN documentos d ON d.id = t.documento_id
        LEFT JOIN vetorial v ON v.id = k.id
        LEFT JOIN textual x ON x.id = k.id
        ORDER BY pontuacao DESC
        LIMIT @candidatos
        """;

    internal void PreencherParametros(NpgsqlCommand cmd, ReadOnlyMemory<float> vetorDaPergunta, string pergunta)
    {
        cmd.Parameters.AddWithValue("vetor", new Vector(vetorDaPergunta));
        cmd.Parameters.Add(new NpgsqlParameter("pergunta", NpgsqlDbType.Text) { Value = pergunta });
        cmd.Parameters.AddWithValue("candidatos", opcoes.CandidatosPorBusca);
    }

    public async Task<IReadOnlyList<TrechoEncontrado>> BuscarAsync(
        ReadOnlyMemory<float> vetorDaPergunta, string pergunta, CancellationToken ct = default)
    {
        await using var conexao = await fonte.OpenConnectionAsync(ct);
        await using var tx = await conexao.BeginTransactionAsync(ct);

        // O HNSW devolve no máximo ef_search vizinhos (padrão 40); com mais candidatos que isso, sobem junto.
        var efSearch = Math.Max(40, opcoes.CandidatosPorBusca).ToString(CultureInfo.InvariantCulture);
        await using (var ajustar = new NpgsqlCommand($"SET LOCAL hnsw.ef_search = {efSearch}", conexao, tx))
            await ajustar.ExecuteNonQueryAsync(ct);

        await using var cmd = new NpgsqlCommand(SqlDaBusca(), conexao, tx);
        PreencherParametros(cmd, vetorDaPergunta, pergunta);

        var lista = new List<TrechoEncontrado>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                lista.Add(new TrechoEncontrado(
                    r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4),
                    Convert.ToDouble(r.GetValue(5), CultureInfo.InvariantCulture),
                    r.IsDBNull(6) ? null : r.GetDouble(6),
                    r.GetDouble(7)));
            }
        }
        await tx.CommitAsync(ct);
        return lista;
    }

    private static async Task Executar(NpgsqlConnection conexao, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conexao);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

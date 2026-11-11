using System.Diagnostics;

namespace RagDocuments.Core;

public sealed class OpcoesRag
{
    public const string Secao = "Rag";

    /// <summary>Nome do modelo de embedding. Fica gravado no banco: trocar de modelo exige reindexar.</summary>
    public string ModeloDeEmbedding { get; set; } = "nomic-embed-text";

    /// <summary>Dimensões do vetor do modelo de embedding (nomic-embed-text: 768; text-embedding-3-small: 1536).</summary>
    public int DimensoesDoEmbedding { get; set; } = 768;

    /// <summary>
    /// Prefixos de tarefa que alguns modelos pedem no texto a embutir. O nomic-embed-text foi treinado com
    /// "search_query: " na pergunta e "search_document: " no documento, e a documentação dele pede os dois.
    /// (No eval deste repositório, em português, eles não mudaram a separação entre perguntas do escopo e
    /// fora dele; veja o README.) Modelos da OpenAI não usam: deixe vazio.
    /// </summary>
    public string PrefixoDaPergunta { get; set; } = string.Empty;

    /// <inheritdoc cref="PrefixoDaPergunta"/>
    public string PrefixoDoDocumento { get; set; } = string.Empty;

    /// <summary>Tamanho alvo de cada trecho, em caracteres.</summary>
    public int TamanhoDoTrecho { get; set; } = 1200;

    /// <summary>Quanto do fim de um trecho se repete no começo do próximo, em caracteres.</summary>
    public int Sobreposicao { get; set; } = 200;

    /// <summary>Quantos trechos vão para o contexto do modelo.</summary>
    public int TrechosNoContexto { get; set; } = 5;

    /// <summary>Quantos candidatos cada busca (vetorial e textual) traz antes da fusão.</summary>
    public int CandidatosPorBusca { get; set; } = 20;

    /// <summary>
    /// Similaridade de cosseno mínima para um trecho contar como relevante pelo significado.
    /// Depende do modelo de embedding: calibre com `dotnet run -- avaliar evals/perguntas.json`.
    /// Sem trecho relevante (por similaridade ou por cobertura textual), a resposta é "não encontrei",
    /// sem chamar o LLM.
    /// </summary>
    public double SimilaridadeMinima { get; set; } = 0.62;

    /// <summary>Fração mínima dos termos da pergunta que precisa aparecer no trecho para ele contar como relevante pelo texto.</summary>
    public double CoberturaTextualMinima { get; set; } = 0.5;

    /// <summary>Configuração de busca textual do PostgreSQL.</summary>
    public string IdiomaDaBuscaTextual { get; set; } = "portuguese";

    /// <summary>Configuração inválida falha na subida, com o motivo, e não na primeira pergunta.</summary>
    public void Validar()
    {
        static void Exigir(bool condicao, string mensagem)
        {
            if (!condicao)
                throw new InvalidOperationException($"Configuração Rag inválida: {mensagem}");
        }

        Exigir(!string.IsNullOrWhiteSpace(ModeloDeEmbedding), "ModeloDeEmbedding é obrigatório.");
        // O índice HNSW do pgvector aceita vetores de até 2.000 dimensões.
        Exigir(DimensoesDoEmbedding is >= 1 and <= 2000, "DimensoesDoEmbedding precisa estar entre 1 e 2000.");
        Exigir(TamanhoDoTrecho is >= 200 and <= 20_000, "TamanhoDoTrecho precisa estar entre 200 e 20000.");
        Exigir(Sobreposicao >= 0 && Sobreposicao <= TamanhoDoTrecho / 2, "Sobreposicao precisa estar entre 0 e metade do TamanhoDoTrecho.");
        Exigir(TrechosNoContexto is >= 1 and <= 20, "TrechosNoContexto precisa estar entre 1 e 20.");
        Exigir(CandidatosPorBusca >= TrechosNoContexto && CandidatosPorBusca <= 200, "CandidatosPorBusca precisa estar entre TrechosNoContexto e 200.");
        Exigir(SimilaridadeMinima is >= 0 and <= 1, "SimilaridadeMinima precisa estar entre 0 e 1.");
        Exigir(CoberturaTextualMinima is > 0 and <= 1, "CoberturaTextualMinima precisa ser maior que 0 e no máximo 1.");
        Exigir(IdiomaDaBuscaTextual.Length > 0 && IdiomaDaBuscaTextual.All(char.IsAsciiLetterLower),
            "IdiomaDaBuscaTextual precisa ser o nome de uma configuração do PostgreSQL, como 'portuguese'.");
    }
}

public static class Telemetria
{
    public const string Nome = "RagDocuments";
    public static readonly ActivitySource Fonte = new(Nome, "2.0.0");
}

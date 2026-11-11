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
    /// Prefixos de tarefa que alguns modelos exigem no texto a embutir. O nomic-embed-text foi treinado com
    /// "search_query: " na pergunta e "search_document: " no documento; sem eles, as similaridades ficam
    /// espremidas e o limiar não separa o que é do escopo. Modelos da OpenAI não usam: deixe vazio.
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
}

public static class Telemetria
{
    public const string Nome = "RagDocuments";
    public static readonly ActivitySource Fonte = new(Nome, "2.0.0");
}

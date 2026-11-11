using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using RagDocuments.Core.Armazenamento;

namespace RagDocuments.Core.Respostas;

public sealed record Fonte(int Numero, Guid DocumentoId, string Documento, string? Secao, string Trecho, double? Similaridade);

public sealed record Uso(long? TokensDeEntrada, long? TokensDeSaida, long MsBusca, long MsGeracao);

/// <param name="Situacao">
/// "respondida": resposta com citações válidas. "nao_encontrada": nada relevante nos documentos (o LLM nem é
/// chamado quando a busca não acha trecho acima do limiar). "sem_citacao": o modelo respondeu sem citar fonte
/// válida; a resposta volta, mas marcada como não verificável.
/// </param>
public sealed record Resposta(string Texto, string Situacao, IReadOnlyList<Fonte> Fontes, int TrechosConsultados, Uso Uso);

public sealed partial class RespostaService(
    BancoVetorial banco,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    IChatClient chat,
    OpcoesRag opcoes)
{
    public async Task<Resposta> ResponderAsync(string pergunta, CancellationToken ct = default)
    {
        using var atividade = Telemetria.Fonte.StartActivity("rag.pergunta");
        if (string.IsNullOrWhiteSpace(pergunta))
            throw new ArgumentException("Informe a pergunta.", nameof(pergunta));

        var relogio = Stopwatch.StartNew();
        var relevantes = await BuscarRelevantesAsync(pergunta, ct);
        var msBusca = relogio.ElapsedMilliseconds;
        atividade?.SetTag("rag.trechos_relevantes", relevantes.Count);

        if (relevantes.Count == 0)
        {
            // Nada acima do limiar: responder "não sei" sem pagar uma chamada ao modelo.
            return new Resposta(Prompt.NaoEncontrei, "nao_encontrada", [], 0, new Uso(0, 0, msBusca, 0));
        }

        relogio.Restart();
        var resposta = await chat.GetResponseAsync(
            Prompt.Montar(pergunta, relevantes),
            new ChatOptions { Temperature = 0 },
            ct);
        var msGeracao = relogio.ElapsedMilliseconds;
        var texto = resposta.Text.Trim();
        var uso = new Uso(resposta.Usage?.InputTokenCount, resposta.Usage?.OutputTokenCount, msBusca, msGeracao);

        if (texto.Contains(Prompt.NaoEncontrei.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
            return new Resposta(Prompt.NaoEncontrei, "nao_encontrada", [], relevantes.Count, uso);

        var citadas = Citacoes(texto, relevantes.Count);
        var fontes = citadas
            .Select(n => (n, t: relevantes[n - 1]))
            .Select(x => new Fonte(x.n, x.t.DocumentoId, x.t.Documento, x.t.Secao, Resumir(x.t.Conteudo), x.t.Similaridade))
            .ToList();

        var situacao = fontes.Count > 0 ? "respondida" : "sem_citacao";
        atividade?.SetTag("rag.situacao", situacao);
        return new Resposta(texto, situacao, fontes, relevantes.Count, uso);
    }

    /// <summary>
    /// Busca híbrida e filtro de relevância: entra o trecho parecido o bastante pelo significado
    /// ou que contém boa parte dos termos da pergunta.
    /// </summary>
    internal async Task<IReadOnlyList<TrechoEncontrado>> BuscarRelevantesAsync(string pergunta, CancellationToken ct)
    {
        var vetor = await embeddings.GenerateVectorAsync(opcoes.PrefixoDaPergunta + pergunta, cancellationToken: ct);
        var candidatos = await banco.BuscarAsync(vetor, pergunta, ct);
        return candidatos
            .Where(c => c.CoberturaTextual >= opcoes.CoberturaTextualMinima || c.Similaridade >= opcoes.SimilaridadeMinima)
            .Take(opcoes.TrechosNoContexto)
            .ToList();
    }

    /// <summary>Números citados que correspondem a um documento enviado; [7] com 5 fontes é descartado.</summary>
    internal static IReadOnlyList<int> Citacoes(string texto, int totalDeFontes) =>
        Citacao().Matches(texto)
            .SelectMany(m => m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Select(s => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .Where(n => n >= 1 && n <= totalDeFontes)
            .Distinct()
            .Order()
            .ToList();

    private static string Resumir(string conteudo) =>
        conteudo.Length <= 300 ? conteudo : string.Concat(conteudo.AsSpan(0, 300), "…");

    [GeneratedRegex(@"\[(\d+(?:\s*,\s*\d+)*)\]")]
    private static partial Regex Citacao();
}

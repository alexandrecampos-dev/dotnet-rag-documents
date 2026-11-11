using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RagDocuments.Core.Armazenamento;
using RagDocuments.Core.Ingestao;
using RagDocuments.Core.Respostas;

namespace RagDocuments.Api;

/// <summary>Uma pergunta do conjunto de avaliação. Documento nulo: a resposta certa é "não encontrei".</summary>
internal sealed record CasoDeAvaliacao(string Pergunta, string? DocumentoEsperado);

internal static class Comandos
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// `dotnet run --project` executa a partir da pasta do projeto; para "exemplos" funcionar a partir
    /// da raiz do repositório, um caminho relativo que não existir aqui é procurado nas pastas acima.
    /// </summary>
    private static string Resolver(string caminho)
    {
        if (Path.IsPathRooted(caminho) || Directory.Exists(caminho) || File.Exists(caminho))
            return caminho;
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            var candidato = Path.Combine(dir.FullName, caminho);
            if (Directory.Exists(candidato) || File.Exists(candidato))
                return candidato;
        }
        return caminho;
    }

    public static async Task<int> IngerirPastaAsync(IServiceProvider services, string pasta)
    {
        pasta = Resolver(pasta);
        if (!Directory.Exists(pasta))
        {
            Console.Error.WriteLine($"Pasta não encontrada: {pasta}");
            return 1;
        }

        await using var scope = services.CreateAsyncScope();
        var ingestao = scope.ServiceProvider.GetRequiredService<IngestaoService>();
        foreach (var arquivo in Directory.EnumerateFiles(pasta, "*.*").Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).Order())
        {
            var r = await ingestao.IngerirAsync(Path.GetFileName(arquivo), await File.ReadAllTextAsync(arquivo), arquivo);
            var estado = r.JaEstavaAtualizado ? "sem mudança" : r.Substituiu ? "substituído" : "novo";
            Console.WriteLine($"{Path.GetFileName(arquivo),-40} {r.Trechos,3} trechos  ({estado})");
        }
        return 0;
    }

    /// <summary>
    /// Avaliação contra o provedor configurado: recall da busca (o documento certo está entre as fontes
    /// consultadas?), taxa de citação válida e recusa correta das perguntas fora do escopo.
    /// </summary>
    public static async Task<int> AvaliarAsync(IServiceProvider services, string arquivo)
    {
        var casos = JsonSerializer.Deserialize<List<CasoDeAvaliacao>>(
            await File.ReadAllTextAsync(Resolver(arquivo)), Json) ?? [];

        await using var scope = services.CreateAsyncScope();
        var respostas = scope.ServiceProvider.GetRequiredService<RespostaService>();
        var banco = scope.ServiceProvider.GetRequiredService<BancoVetorial>();
        var embeddings = scope.ServiceProvider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        int dentro = 0, achou = 0, citou = 0, fora = 0, recusou = 0;
        foreach (var caso in casos)
        {
            // Para calibrar o limiar: a melhor similaridade e a melhor cobertura que a busca achou.
            var opcoesRag = scope.ServiceProvider.GetRequiredService<RagDocuments.Core.OpcoesRag>();
            var candidatos = await banco.BuscarAsync(await embeddings.GenerateVectorAsync(opcoesRag.PrefixoDaPergunta + caso.Pergunta), caso.Pergunta);
            var sinal = string.Create(CultureInfo.InvariantCulture,
                $"sim={candidatos.Max(c => c.Similaridade) ?? 0:0.00} cob={candidatos.Select(c => c.CoberturaTextual).DefaultIfEmpty(0).Max():0.00}");

            var r = await respostas.ResponderAsync(caso.Pergunta);
            if (caso.DocumentoEsperado is null)
            {
                fora++;
                var ok = r.Situacao == "nao_encontrada";
                recusou += ok ? 1 : 0;
                Console.WriteLine($"[{(ok ? "ok" : "FALHOU")}] {sinal}  fora do escopo: {caso.Pergunta}");
                continue;
            }

            dentro++;
            var acertouFonte = r.Fontes.Any(f => f.Documento == caso.DocumentoEsperado);
            achou += acertouFonte ? 1 : 0;
            citou += r.Situacao == "respondida" ? 1 : 0;
            Console.WriteLine($"[{(acertouFonte ? "ok" : "FALHOU")}] {sinal}  {caso.Pergunta} -> {r.Situacao}");
            Console.WriteLine($"      {r.Texto.ReplaceLineEndings(" ")}");
        }

        string Pct(int a, int b) => b == 0 ? "-" : (100.0 * a / b).ToString("0", CultureInfo.InvariantCulture) + "%";
        Console.WriteLine();
        Console.WriteLine($"Documento certo citado:      {Pct(achou, dentro)} ({achou}/{dentro})");
        Console.WriteLine($"Respostas com citação válida: {Pct(citou, dentro)} ({citou}/{dentro})");
        Console.WriteLine($"Recusa fora do escopo:        {Pct(recusou, fora)} ({recusou}/{fora})");
        return achou == dentro && recusou == fora ? 0 : 2;
    }
}

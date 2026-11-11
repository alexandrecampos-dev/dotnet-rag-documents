using System.Text;
using System.Text.RegularExpressions;

namespace RagDocuments.Core.Ingestao;

public sealed record Trecho(int Ordem, string? Secao, string Conteudo)
{
    /// <summary>
    /// Texto usado no embedding: o título da seção vai junto, porque "o prazo é de 30 dias" sozinho
    /// não diz prazo de quê. Na resposta e na busca textual o conteúdo segue sem alteração.
    /// </summary>
    public string TextoParaEmbedding => Secao is null ? Conteudo : $"{Secao}\n\n{Conteudo}";
}

/// <summary>
/// Divide o documento respeitando a estrutura: primeiro por seção (títulos Markdown), depois por
/// parágrafo, e só quebra parágrafo longo em fronteira de frase. Texto sem pontuação (lista, tabela,
/// código, tudo em minúsculas) cai no último recurso: corte por palavra e, se preciso, por caractere.
/// Nenhum trecho passa de TamanhoDoTrecho + Sobreposicao. A 1.0 cortava a cada N palavras, no meio
/// da frase e sem saber de que seção era o texto.
/// </summary>
public static partial class Fatiador
{
    public static IReadOnlyList<Trecho> Fatiar(string texto, int tamanho, int sobreposicao)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tamanho, 200);
        sobreposicao = Math.Clamp(sobreposicao, 0, tamanho / 2);

        var trechos = new List<Trecho>();
        foreach (var (secao, corpo) in Secoes(texto.Replace("\r\n", "\n", StringComparison.Ordinal)))
        {
            var atual = new StringBuilder();
            foreach (var bloco in Blocos(corpo, tamanho))
            {
                if (atual.Length > 0 && atual.Length + bloco.Length + 2 > tamanho)
                {
                    trechos.Add(new Trecho(trechos.Count, secao, atual.ToString().Trim()));
                    var cauda = Cauda(atual.ToString(), sobreposicao);
                    atual.Clear();
                    if (cauda.Length > 0)
                        atual.Append(cauda).Append("\n\n");
                }
                atual.Append(bloco).Append("\n\n");
            }
            if (atual.ToString().Trim().Length > 0)
                trechos.Add(new Trecho(trechos.Count, secao, atual.ToString().Trim()));
        }
        return trechos;
    }

    /// <summary>
    /// Seções delimitadas por títulos Markdown; o texto antes do primeiro título fica sem seção.
    /// Linhas com # dentro de bloco de código (comentário de shell ou Python) não são título.
    /// </summary>
    private static IEnumerable<(string? Secao, string Corpo)> Secoes(string texto)
    {
        string? titulo = null;
        var corpo = new StringBuilder();
        var dentroDeCodigo = false;
        foreach (var linha in texto.Split('\n'))
        {
            var aparada = linha.TrimStart();
            if (aparada.StartsWith("```", StringComparison.Ordinal) || aparada.StartsWith("~~~", StringComparison.Ordinal))
                dentroDeCodigo = !dentroDeCodigo;

            var m = dentroDeCodigo ? Match.Empty : Titulo().Match(linha);
            if (m.Success)
            {
                if (corpo.ToString().Trim().Length > 0)
                    yield return (titulo, corpo.ToString());
                titulo = m.Groups[1].Value.Trim();
                corpo.Clear();
            }
            else
            {
                corpo.Append(linha).Append('\n');
            }
        }
        if (corpo.ToString().Trim().Length > 0)
            yield return (titulo, corpo.ToString());
    }

    /// <summary>Parágrafos; parágrafo maior que o trecho é quebrado em frases e, no limite, por palavra.</summary>
    private static IEnumerable<string> Blocos(string corpo, int tamanho)
    {
        foreach (var paragrafo in Paragrafo().Split(corpo).Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            if (paragrafo.Length <= tamanho)
            {
                yield return paragrafo;
                continue;
            }

            var pedaco = new StringBuilder();
            foreach (var frase in Frases(paragrafo).SelectMany(f => Cortar(f, tamanho)))
            {
                if (pedaco.Length > 0 && pedaco.Length + frase.Length + 1 > tamanho)
                {
                    yield return pedaco.ToString().Trim();
                    pedaco.Clear();
                }
                pedaco.Append(frase).Append(' ');
            }
            if (pedaco.Length > 0)
                yield return pedaco.ToString().Trim();
        }
    }

    /// <summary>Último recurso para texto sem fim de frase: corta por palavra; palavra maior que o trecho, por caractere.</summary>
    private static IEnumerable<string> Cortar(string frase, int tamanho)
    {
        if (frase.Length <= tamanho)
        {
            yield return frase;
            yield break;
        }

        var pedaco = new StringBuilder();
        foreach (var palavra in frase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (palavra.Length > tamanho)
            {
                if (pedaco.Length > 0)
                {
                    yield return pedaco.ToString().Trim();
                    pedaco.Clear();
                }
                for (var i = 0; i < palavra.Length; i += tamanho)
                    yield return palavra.Substring(i, Math.Min(tamanho, palavra.Length - i));
                continue;
            }
            if (pedaco.Length + palavra.Length + 1 > tamanho)
            {
                yield return pedaco.ToString().Trim();
                pedaco.Clear();
            }
            pedaco.Append(palavra).Append(' ');
        }
        if (pedaco.Length > 0)
            yield return pedaco.ToString().Trim();
    }

    private static IEnumerable<string> Frases(string paragrafo) =>
        FimDeFrase().Split(paragrafo).Select(f => f.Trim()).Where(f => f.Length > 0);

    /// <summary>As últimas frases inteiras que cabem na sobreposição.</summary>
    private static string Cauda(string texto, int sobreposicao)
    {
        if (sobreposicao == 0)
            return string.Empty;
        var frases = Frases(texto.Replace("\n\n", " ", StringComparison.Ordinal)).ToList();
        var cauda = new List<string>();
        var total = 0;
        for (var i = frases.Count - 1; i >= 0 && total + frases[i].Length <= sobreposicao; i--)
        {
            cauda.Insert(0, frases[i]);
            total += frases[i].Length + 1;
        }
        return string.Join(' ', cauda);
    }

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$")]
    private static partial Regex Titulo();

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex Paragrafo();

    [GeneratedRegex(@"(?<=[.!?…])\s+(?=[A-ZÀ-Ý0-9""“(])")]
    private static partial Regex FimDeFrase();
}

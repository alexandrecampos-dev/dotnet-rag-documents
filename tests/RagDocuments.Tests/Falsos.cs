using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;

namespace RagDocuments.Tests;

/// <summary>
/// Embedding determinístico para teste: saco de palavras (sem acento, sem palavra vazia, radical de 5 letras)
/// espalhado por hash num vetor normalizado. Não entende sinônimo, mas é estável e não depende de rede,
/// o que basta para testar o pipeline (fatiamento, gravação, fusão, limiar, citação).
/// </summary>
public sealed class EmbeddingDeTeste(int dimensoes = 256) : IEmbeddingGenerator<string, Embedding<float>>
{
    private static readonly HashSet<string> Vazias =
    [
        "a", "o", "as", "os", "de", "da", "do", "das", "dos", "e", "em", "no", "na", "nos", "nas", "um", "uma",
        "para", "por", "com", "que", "qual", "quais", "como", "meu", "minha", "minhas", "meus", "posso", "preciso",
        "faco", "se", "ou", "ao", "aos", "pelo", "pela", "sao", "ser", "tem", "quanto", "quanta", "quantos",
    ];

    public int Chamadas { get; private set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        Chamadas++;
        var gerados = new GeneratedEmbeddings<Embedding<float>>(values.Select(v => new Embedding<float>(Vetor(v))));
        return Task.FromResult(gerados);
    }

    private float[] Vetor(string texto)
    {
        var v = new float[dimensoes];
        foreach (var palavra in Palavras(texto))
        {
            // GetHashCode de string muda a cada processo; FNV-1a é estável entre execuções.
            v[Fnv(palavra) % (uint)dimensoes] += 1;
        }
        var norma = MathF.Sqrt(v.Sum(x => x * x));
        if (norma > 0)
            for (var i = 0; i < v.Length; i++) v[i] /= norma;
        else
            v[0] = 1;
        return v;
    }

    internal static IEnumerable<string> Palavras(string texto)
    {
        var limpo = new StringBuilder();
        foreach (var c in texto.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            limpo.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return limpo.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Length > 2 && !Vazias.Contains(p))
            .Select(p => p.Length > 5 ? p[..5] : p);
    }

    private static uint Fnv(string s)
    {
        var h = 2166136261u;
        foreach (var c in s) h = (h ^ c) * 16777619u;
        return h;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>Chat de teste: devolve uma resposta programada e guarda as mensagens que recebeu.</summary>
public sealed class ChatDeTeste : IChatClient
{
    public Func<IReadOnlyList<ChatMessage>, string> Responder { get; set; } = _ => "Conforme o documento [1].";
    public IReadOnlyList<ChatMessage>? UltimasMensagens { get; private set; }
    public int Chamadas { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Chamadas++;
        UltimasMensagens = messages.ToList();
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Responder(UltimasMensagens)))
        {
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20 },
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var r = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var u in r.ToChatResponseUpdates()) yield return u;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

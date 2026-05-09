using MediatR;
using RagDocuments.Application.Common.Interfaces;

namespace RagDocuments.Application.Features.Query.Commands;

public record QueryDocumentsCommand(string Question, int TopK = 5) : IRequest<QueryDocumentsResult>;

public record RetrievedSource(Guid DocumentId, string DocumentName, string Excerpt, double Score);

public record QueryDocumentsResult(string Answer, IReadOnlyList<RetrievedSource> Sources);

public class QueryDocumentsCommandHandler : IRequestHandler<QueryDocumentsCommand, QueryDocumentsResult>
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorSearchService _vectorSearch;
    private readonly ILlmService _llm;

    public QueryDocumentsCommandHandler(
        IEmbeddingService embeddingService,
        IVectorSearchService vectorSearch,
        ILlmService llm)
    {
        _embeddingService = embeddingService;
        _vectorSearch = vectorSearch;
        _llm = llm;
    }

    public async Task<QueryDocumentsResult> Handle(QueryDocumentsCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            throw new ArgumentException("Question is required.", nameof(request.Question));

        var topK = Math.Clamp(request.TopK, 1, 20);
        var queryEmbedding = await _embeddingService.GenerateAsync(request.Question, cancellationToken);

        var similarChunks = await _vectorSearch.SearchAsync(queryEmbedding, topK, cancellationToken);

        if (similarChunks.Count == 0)
        {
            return new QueryDocumentsResult(
                "No relevant documents found for the given question.",
                Array.Empty<RetrievedSource>());
        }

        var context = BuildContext(similarChunks);

        var systemPrompt =
            "You are a precise assistant that answers questions exclusively based on the provided context. " +
            "If the answer cannot be found in the context, state that clearly. " +
            "Do not infer or add information that is not present in the context.";

        var userPrompt =
            $"Context:\n{context}\n\n" +
            $"Question: {request.Question}";

        var answer = await _llm.CompleteAsync(systemPrompt, userPrompt, cancellationToken);

        var sources = similarChunks
            .Select(c => new RetrievedSource(c.DocumentId, c.DocumentName, TruncateExcerpt(c.Content), c.Score))
            .ToList();

        return new QueryDocumentsResult(answer, sources);
    }

    private static string BuildContext(IReadOnlyList<SimilarChunk> chunks)
    {
        return string.Join("\n\n---\n\n", chunks.Select((c, i) =>
            $"[Source {i + 1}: {c.DocumentName}]\n{c.Content}"));
    }

    private static string TruncateExcerpt(string content, int maxLength = 200)
    {
        return content.Length <= maxLength ? content : content[..maxLength] + "...";
    }
}

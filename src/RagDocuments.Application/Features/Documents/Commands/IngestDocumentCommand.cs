using MediatR;
using RagDocuments.Application.Common.Interfaces;
using RagDocuments.Domain.Entities;

namespace RagDocuments.Application.Features.Documents.Commands;

public record IngestDocumentCommand(string Name, string Source, string Content, string ContentType) : IRequest<IngestDocumentResult>;

public record IngestDocumentResult(Guid DocumentId, int ChunksCreated);

public class IngestDocumentCommandHandler : IRequestHandler<IngestDocumentCommand, IngestDocumentResult>
{
    private readonly IApplicationDbContext _context;
    private readonly IEmbeddingService _embeddingService;
    private readonly ITextChunker _chunker;

    public IngestDocumentCommandHandler(
        IApplicationDbContext context,
        IEmbeddingService embeddingService,
        ITextChunker chunker)
    {
        _context = context;
        _embeddingService = embeddingService;
        _chunker = chunker;
    }

    public async Task<IngestDocumentResult> Handle(IngestDocumentCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Document name is required.", nameof(request.Name));
        if (string.IsNullOrWhiteSpace(request.Content))
            throw new ArgumentException("Document content is required.", nameof(request.Content));

        var document = new Document(request.Name, request.Source, request.ContentType);
        await _context.Documents.AddAsync(document, cancellationToken);

        var chunks = _chunker.Chunk(request.Content);
        var embeddings = await _embeddingService.GenerateBatchAsync(chunks, cancellationToken);

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = new DocumentChunk(document.Id, chunks[i], i, embeddings[i]);
            await _context.DocumentChunks.AddAsync(chunk, cancellationToken);
        }

        document.SetTotalChunks(chunks.Count);
        await _context.SaveChangesAsync(cancellationToken);

        return new IngestDocumentResult(document.Id, chunks.Count);
    }
}

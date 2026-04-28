namespace RagDocuments.Application.Common.Interfaces;

public record SimilarChunk(Guid ChunkId, Guid DocumentId, string DocumentName, string Content, double Score);

public interface IVectorSearchService
{
    Task<IReadOnlyList<SimilarChunk>> SearchAsync(float[] queryEmbedding, int topK = 5, CancellationToken cancellationToken = default);
}

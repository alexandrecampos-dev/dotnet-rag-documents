using RagDocuments.Domain.Common;

namespace RagDocuments.Domain.Entities;

public class DocumentChunk : BaseEntity
{
    public Guid DocumentId { get; private set; }
    public Document Document { get; private set; } = null!;
    public string Content { get; private set; } = string.Empty;
    public int ChunkIndex { get; private set; }
    public float[] Embedding { get; private set; } = Array.Empty<float>();

    private DocumentChunk() { }

    public DocumentChunk(Guid documentId, string content, int chunkIndex, float[] embedding)
    {
        DocumentId = documentId;
        Content = content;
        ChunkIndex = chunkIndex;
        Embedding = embedding;
    }
}

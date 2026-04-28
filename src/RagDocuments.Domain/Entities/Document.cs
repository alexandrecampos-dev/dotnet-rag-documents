using RagDocuments.Domain.Common;

namespace RagDocuments.Domain.Entities;

public class Document : BaseEntity
{
    public string Name { get; private set; } = string.Empty;
    public string Source { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public int TotalChunks { get; private set; }

    private readonly List<DocumentChunk> _chunks = new();
    public IReadOnlyCollection<DocumentChunk> Chunks => _chunks.AsReadOnly();

    private Document() { }

    public Document(string name, string source, string contentType)
    {
        Name = name;
        Source = source;
        ContentType = contentType;
    }

    public void SetTotalChunks(int count)
    {
        TotalChunks = count;
    }
}

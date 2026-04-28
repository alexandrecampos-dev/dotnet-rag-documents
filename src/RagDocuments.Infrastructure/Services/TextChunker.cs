using Microsoft.Extensions.Options;
using RagDocuments.Application.Common.Interfaces;
using RagDocuments.Infrastructure.Settings;

namespace RagDocuments.Infrastructure.Services;

public class TextChunker : ITextChunker
{
    private readonly int _chunkSize;
    private readonly int _overlapSize;

    public TextChunker(IOptions<ChunkingSettings> options)
    {
        _chunkSize = options.Value.ChunkSize;
        _overlapSize = options.Value.OverlapSize;
    }

    public IReadOnlyList<string> Chunk(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var chunks = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var start = 0;
        while (start < words.Length)
        {
            var end = Math.Min(start + _chunkSize, words.Length);
            var chunk = string.Join(' ', words[start..end]).Trim();

            if (!string.IsNullOrWhiteSpace(chunk))
                chunks.Add(chunk);

            if (end == words.Length)
                break;

            start += _chunkSize - _overlapSize;
        }

        return chunks;
    }
}

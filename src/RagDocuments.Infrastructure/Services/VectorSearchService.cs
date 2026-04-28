using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;
using RagDocuments.Application.Common.Interfaces;
using RagDocuments.Infrastructure.Persistence;

namespace RagDocuments.Infrastructure.Services;

public class VectorSearchService : IVectorSearchService
{
    private readonly ApplicationDbContext _context;

    public VectorSearchService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<SimilarChunk>> SearchAsync(
        float[] queryEmbedding,
        int topK = 5,
        CancellationToken cancellationToken = default)
    {
        var vector = new Vector(queryEmbedding);

        // Uses pgvector cosine distance operator (<=>). Lower distance = more similar.
        // Score is normalized to [0, 1] where 1 = identical.
        var sql = $"""
            SELECT
                c."Id"           AS "ChunkId",
                c."DocumentId"   AS "DocumentId",
                d."Name"         AS "DocumentName",
                c."Content"      AS "Content",
                1 - (c."Embedding" <=> @queryVector::vector) AS "Score"
            FROM "DocumentChunks" c
            JOIN "Documents" d ON d."Id" = c."DocumentId"
            ORDER BY c."Embedding" <=> @queryVector::vector
            LIMIT @topK
            """;

        var results = await _context.Database
            .SqlQueryRaw<ChunkSearchResult>(
                sql,
                new NpgsqlParameter("queryVector", vector),
                new NpgsqlParameter("topK", topK))
            .ToListAsync(cancellationToken);

        return results
            .Select(r => new SimilarChunk(r.ChunkId, r.DocumentId, r.DocumentName, r.Content, r.Score))
            .ToList();
    }

    private record ChunkSearchResult(Guid ChunkId, Guid DocumentId, string DocumentName, string Content, double Score);
}

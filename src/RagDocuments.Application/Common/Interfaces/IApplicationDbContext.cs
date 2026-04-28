using Microsoft.EntityFrameworkCore;
using RagDocuments.Domain.Entities;

namespace RagDocuments.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    DbSet<Document> Documents { get; }
    DbSet<DocumentChunk> DocumentChunks { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

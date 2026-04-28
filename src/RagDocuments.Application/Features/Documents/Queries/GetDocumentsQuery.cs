using MediatR;
using Microsoft.EntityFrameworkCore;
using RagDocuments.Application.Common.Interfaces;

namespace RagDocuments.Application.Features.Documents.Queries;

public record GetDocumentsQuery : IRequest<IReadOnlyList<DocumentSummary>>;

public record DocumentSummary(Guid Id, string Name, string Source, string ContentType, int TotalChunks, DateTime IngestedAt);

public class GetDocumentsQueryHandler : IRequestHandler<GetDocumentsQuery, IReadOnlyList<DocumentSummary>>
{
    private readonly IApplicationDbContext _context;

    public GetDocumentsQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<DocumentSummary>> Handle(GetDocumentsQuery request, CancellationToken cancellationToken)
    {
        return await _context.Documents
            .AsNoTracking()
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new DocumentSummary(d.Id, d.Name, d.Source, d.ContentType, d.TotalChunks, d.CreatedAt))
            .ToListAsync(cancellationToken);
    }
}

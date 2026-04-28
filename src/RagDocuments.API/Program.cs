using MediatR;
using Microsoft.EntityFrameworkCore;
using RagDocuments.Application;
using RagDocuments.Application.Features.Documents.Commands;
using RagDocuments.Application.Features.Documents.Queries;
using RagDocuments.Application.Features.Query.Commands;
using RagDocuments.Infrastructure;
using RagDocuments.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddOpenApi();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.Migrate();
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

// List all ingested documents
app.MapGet("/documents", async (IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new GetDocumentsQuery(), ct);
    return Results.Ok(result);
})
.WithName("GetDocuments")
.WithSummary("Returns all ingested documents with their metadata.");

// Ingest a document
app.MapPost("/documents", async (IngestRequest request, IMediator mediator, CancellationToken ct) =>
{
    var command = new IngestDocumentCommand(
        request.Name,
        request.Source ?? string.Empty,
        request.Content,
        request.ContentType ?? "text/plain");

    var result = await mediator.Send(command, ct);
    return Results.Created($"/documents/{result.DocumentId}", result);
})
.WithName("IngestDocument")
.WithSummary("Ingests a document: chunks the content, generates embeddings and stores in pgvector.");

// Query documents
app.MapPost("/query", async (QueryRequest request, IMediator mediator, CancellationToken ct) =>
{
    var command = new QueryDocumentsCommand(request.Question, request.TopK ?? 5);
    var result = await mediator.Send(command, ct);
    return Results.Ok(result);
})
.WithName("QueryDocuments")
.WithSummary("Runs a RAG query: embeds the question, retrieves similar chunks and returns an LLM-generated answer with sources.");

app.Run();

record IngestRequest(string Name, string Content, string? Source, string? ContentType);
record QueryRequest(string Question, int? TopK);

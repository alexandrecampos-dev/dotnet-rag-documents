using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using RagDocuments.Application.Common.Interfaces;
using RagDocuments.Domain.Entities;
using RagDocuments.Infrastructure.Settings;

namespace RagDocuments.Infrastructure.Persistence;

public class ApplicationDbContext : DbContext, IApplicationDbContext
{
    private readonly int _vectorDimensions;

    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options,
        IOptions<VectorDimensionOptions> vectorOptions) : base(options)
    {
        _vectorDimensions = vectorOptions.Value.Dimensions;
    }

    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<Document>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Name).IsRequired().HasMaxLength(500);
            entity.Property(d => d.Source).HasMaxLength(1000);
            entity.Property(d => d.ContentType).HasMaxLength(100);
            entity.HasMany(d => d.Chunks)
                  .WithOne(c => c.Document)
                  .HasForeignKey(c => c.DocumentId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DocumentChunk>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Content).IsRequired();
            entity.Property(c => c.Embedding)
                  .HasColumnType($"vector({_vectorDimensions})")
                  .HasConversion(
                      v => new Vector(v),
                      v => v.ToArray());

            entity.HasIndex(c => c.DocumentId);
        });
    }
}

namespace RagDocuments.Infrastructure.Settings;

public class ChunkingSettings
{
    public const string SectionName = "Chunking";

    /// <summary>Number of words per chunk.</summary>
    public int ChunkSize { get; init; } = 300;

    /// <summary>Number of words shared between consecutive chunks.</summary>
    public int OverlapSize { get; init; } = 50;
}

namespace RagDocuments.Application.Common.Interfaces;

public interface ILlmService
{
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);
}

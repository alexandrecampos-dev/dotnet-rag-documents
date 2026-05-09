using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using RagDocuments.Application.Common.Interfaces;
using RagDocuments.Infrastructure.Settings;

namespace RagDocuments.Infrastructure.Services.Llm;

public class AzureOpenAILlmService : ILlmService
{
    private readonly ChatClient _client;

    public AzureOpenAILlmService(IOptions<AzureOpenAISettings> options)
    {
        var settings = options.Value;

        var azureClient = new AzureOpenAIClient(
            new Uri(settings.Endpoint),
            new AzureKeyCredential(settings.ApiKey));

        _client = azureClient.GetChatClient(settings.ChatDeployment);
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };

        var response = await _client.CompleteChatAsync(messages, cancellationToken: cancellationToken);
        var content = response.Value.Content;
        if (content is not { Count: > 0 })
            throw new InvalidOperationException("Empty response from Azure OpenAI.");
        return content[0].Text;
    }
}

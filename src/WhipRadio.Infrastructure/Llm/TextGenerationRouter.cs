using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Infrastructure.Llm;

/// <summary>
/// Settings-driven text provider switch: "ollama" (local, default) or "openai".
/// Falls back to Ollama when OpenAI is selected without an API key.
/// </summary>
public class TextGenerationRouter(
    IHttpClientFactory httpClientFactory,
    IOptions<LlmOptions> llmOptions,
    StationSettingsCache settingsCache,
    ILogger<TextGenerationRouter> logger,
    ILoggerFactory loggerFactory) : ITextGenerationService
{
    public const string OllamaClientName = "llm-ollama";
    public const string OpenAiClientName = "llm-openai";

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var settings = await settingsCache.GetAsync(ct);

        if (settings.TextProvider == TextProviders.OpenAi && !string.IsNullOrWhiteSpace(settings.OpenAiApiKey))
        {
            logger.LogInformation("Writer Room provider selected: OpenAI model {Model}", settings.OpenAiModel);
            var openAi = new OpenAiTextGenerationService(
                httpClientFactory.CreateClient(OpenAiClientName), settings.OpenAiApiKey, settings.OpenAiModel);
            return await openAi.CompleteAsync(systemPrompt, userPrompt, ct);
        }

        if (settings.TextProvider == TextProviders.OpenAi)
        {
            logger.LogWarning(
                "Writer Room OpenAI selected but no API key is configured; falling back to Ollama model {Model}",
                llmOptions.Value.Model);
        }

        var ollama = new OllamaTextGenerationService(
            httpClientFactory.CreateClient(OllamaClientName),
            llmOptions,
            loggerFactory.CreateLogger<OllamaTextGenerationService>());
        return await ollama.CompleteAsync(systemPrompt, userPrompt, ct);
    }
}

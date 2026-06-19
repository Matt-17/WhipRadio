using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Infrastructure.Studios;

namespace WhipRadio.Infrastructure.Llm;

/// <summary>
/// Settings-driven text provider switch: "ollama" (local, default) or "openai".
/// Falls back to Ollama when OpenAI is selected without an API key.
/// </summary>
public class TextGenerationRouter(
    IHttpClientFactory httpClientFactory,
    IOptions<LlmOptions> llmOptions,
    StationSettingsCache settingsCache,
    StudioCoordinator studios,
    StudioHistoryRecorder history,
    OllamaModelMemoryManager modelMemory,
    ILogger<TextGenerationRouter> logger,
    ILoggerFactory loggerFactory) : ITextGenerationService
{
    public const string OllamaClientName = "llm-ollama";
    public const string OpenAiClientName = "llm-openai";

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        => await CompleteAsync(systemPrompt, userPrompt, jobLabel: null, ct);

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, string? jobLabel, CancellationToken ct)
    {
        var label = NormalizeJobLabel(jobLabel);
        var settings = await settingsCache.GetAsync(ct);
        var writerRoom = await AcquireWriterRoomAsync(settings, label, ct);
        if (writerRoom is not null)
        {
            var success = false;
            try
            {
                var result = await CompleteWithWriterRoomAsync(writerRoom, settings, systemPrompt, userPrompt, label, ct);
                success = true;
                return result;
            }
            finally
            {
                await studios.ReleaseAsync(writerRoom.Id, success, CancellationToken.None);
            }
        }

        if (settings.TextProvider == TextProviders.OpenAi && !string.IsNullOrWhiteSpace(settings.OpenAiApiKey))
        {
            logger.LogInformation("Writer Room provider selected: OpenAI model {Model}", settings.OpenAiModel);
            return await CompleteWithHistoryAsync(
                studioId: null,
                studioName: "Writer Room (OpenAI settings)",
                provider: StudioProviders.OpenAi,
                model: settings.OpenAiModel,
                endpoint: "https://api.openai.com",
                label,
                systemPrompt,
                userPrompt,
                async token =>
                {
                    var openAi = new OpenAiTextGenerationService(
                        httpClientFactory.CreateClient(OpenAiClientName), settings.OpenAiApiKey, settings.OpenAiModel);
                    return await openAi.CompleteAsync(systemPrompt, userPrompt, token);
                },
                ct);
        }

        if (settings.TextProvider == TextProviders.OpenAi)
        {
            logger.LogWarning(
                "Writer Room OpenAI selected but no API key is configured; falling back to Ollama model {Model}",
                llmOptions.Value.Model);
        }

        return await CompleteWithHistoryAsync(
            studioId: null,
            studioName: "Writer Room (Ollama default)",
            provider: StudioProviders.Ollama,
            model: llmOptions.Value.Model,
            endpoint: httpClientFactory.CreateClient(OllamaClientName).BaseAddress?.ToString(),
            label,
            systemPrompt,
            userPrompt,
            async token =>
            {
                var client = httpClientFactory.CreateClient(OllamaClientName);
                await modelMemory.TryPrepareForLocalGpuJobAsync(
                    client.BaseAddress?.ToString(), unloadOllama: false, unloadLocalTts: true, token);
                var ollama = new OllamaTextGenerationService(
                    client,
                    llmOptions,
                    loggerFactory.CreateLogger<OllamaTextGenerationService>());
                return await ollama.CompleteAsync(systemPrompt, userPrompt, token);
            },
            ct);
    }

    private async Task<Studio?> AcquireWriterRoomAsync(StationSettings settings, string label, CancellationToken ct)
    {
        var preferredProvider = settings.TextProvider == TextProviders.OpenAi
            ? StudioProviders.OpenAi
            : StudioProviders.Ollama;

        while (await studios.AnyActiveAsync(StudioKind.WriterRoom, requiredProvider: null, ct))
        {
            var hasPreferredRooms = await studios.AnyActiveAsync(StudioKind.WriterRoom, preferredProvider, ct);
            if (hasPreferredRooms)
            {
                var preferredRoom = await studios.TryAcquireAsync(
                    StudioKind.WriterRoom, preferredProvider, label, ct);
                if (preferredRoom is not null)
                {
                    return preferredRoom;
                }

                if (await studios.AnyBusyAsync(StudioKind.WriterRoom, preferredProvider, ct)
                    || await studios.AnyAvailableAsync(StudioKind.WriterRoom, preferredProvider, ct))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                    continue;
                }
            }

            if (settings.TextProvider == TextProviders.OpenAi
                && !string.IsNullOrWhiteSpace(settings.OpenAiApiKey))
            {
                return null;
            }

            var writerRoom = await studios.TryAcquireAsync(
                StudioKind.WriterRoom, requiredProvider: null, label, ct);
            if (writerRoom is not null)
            {
                return writerRoom;
            }

            if (!await studios.AnyBusyAsync(StudioKind.WriterRoom, requiredProvider: null, ct)
                && !await studios.AnyAvailableAsync(StudioKind.WriterRoom, requiredProvider: null, ct))
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }

        return null;
    }

    private async Task<string> CompleteWithWriterRoomAsync(
        Studio writerRoom,
        StationSettings settings,
        string systemPrompt,
        string userPrompt,
        string label,
        CancellationToken ct)
    {
        if (string.Equals(writerRoom.Provider, StudioProviders.OpenAi, StringComparison.OrdinalIgnoreCase))
        {
            var apiKey = string.IsNullOrWhiteSpace(writerRoom.ApiKey)
                ? settings.OpenAiApiKey
                : writerRoom.ApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException($"{writerRoom.Name} is missing an OpenAI API key.");
            }

            logger.LogInformation("Writer Room selected: {WriterRoom} (OpenAI model {Model})",
                writerRoom.Name, settings.OpenAiModel);
            return await CompleteWithHistoryAsync(
                writerRoom.Id,
                writerRoom.Name,
                writerRoom.Provider,
                settings.OpenAiModel,
                "https://api.openai.com",
                label,
                systemPrompt,
                userPrompt,
                async token =>
                {
                    var openAi = new OpenAiTextGenerationService(
                        httpClientFactory.CreateClient(OpenAiClientName), apiKey, settings.OpenAiModel);
                    return await openAi.CompleteAsync(systemPrompt, userPrompt, token);
                },
                ct);
        }

        if (!string.Equals(writerRoom.Provider, StudioProviders.Ollama, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{writerRoom.Name} uses unsupported writer room provider '{writerRoom.Provider}'.");
        }

        var client = httpClientFactory.CreateClient(OllamaClientName);
        if (!string.IsNullOrWhiteSpace(writerRoom.Url))
        {
            client.BaseAddress = new Uri(writerRoom.Url);
        }

        logger.LogInformation("Writer Room selected: {WriterRoom} (Ollama model {Model})",
            writerRoom.Name, llmOptions.Value.Model);
        await modelMemory.TryPrepareForLocalGpuJobAsync(
            client.BaseAddress?.ToString(), unloadOllama: false, unloadLocalTts: true, ct);
        return await CompleteWithHistoryAsync(
            writerRoom.Id,
            writerRoom.Name,
            writerRoom.Provider,
            llmOptions.Value.Model,
            client.BaseAddress?.ToString(),
            label,
            systemPrompt,
            userPrompt,
            async token =>
            {
                var ollama = new OllamaTextGenerationService(
                    client,
                    llmOptions,
                    loggerFactory.CreateLogger<OllamaTextGenerationService>());
                return await ollama.CompleteAsync(systemPrompt, userPrompt, token);
            },
            ct);
    }

    private async Task<string> CompleteWithHistoryAsync(
        Guid? studioId,
        string studioName,
        string provider,
        string model,
        string? endpoint,
        string label,
        string systemPrompt,
        string userPrompt,
        Func<CancellationToken, Task<string>> complete,
        CancellationToken ct)
    {
        var historyId = await history.BeginAsync(
            studioId,
            studioName,
            StudioKind.WriterRoom,
            provider,
            label,
            WriterPrompt(systemPrompt, userPrompt),
            WriterDetail(model, endpoint),
            ct);

        try
        {
            var result = await complete(ct);
            await history.CompleteAsync(historyId, result, null, CancellationToken.None);
            return result;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await history.FailAsync(historyId, ex, null, CancellationToken.None);
            throw;
        }
    }

    private static string WriterPrompt(string systemPrompt, string userPrompt)
        => $"System prompt:{Environment.NewLine}{systemPrompt}{Environment.NewLine}{Environment.NewLine}User prompt:{Environment.NewLine}{userPrompt}";

    private static string NormalizeJobLabel(string? label)
        => string.IsNullOrWhiteSpace(label) ? "Writing text" : label.Trim();

    private static string WriterDetail(string model, string? endpoint)
        => string.IsNullOrWhiteSpace(endpoint)
            ? $"Model: {model}"
            : $"Model: {model}{Environment.NewLine}Endpoint: {endpoint.TrimEnd('/')}";
}

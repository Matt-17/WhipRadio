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
    ILogger<TextGenerationRouter> logger,
    ILoggerFactory loggerFactory) : ITextGenerationService
{
    public const string OllamaClientName = "llm-ollama";
    public const string OpenAiClientName = "llm-openai";

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        => await CompleteAsync(new TextGenerationRequest(systemPrompt, userPrompt), ct);

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, string? jobLabel, CancellationToken ct)
        => await CompleteAsync(new TextGenerationRequest(systemPrompt, userPrompt, jobLabel), ct);

    public async Task<string> CompleteAsync(TextGenerationRequest generation, CancellationToken ct)
    {
        var label = NormalizeJobLabel(generation.JobLabel);
        var settings = await settingsCache.GetAsync(ct);
        var preferOpenAi = settings.TextProvider == TextProviders.OpenAi;
        var preferredProvider = preferOpenAi ? StudioProviders.OpenAi : StudioProviders.Ollama;

        // Prefer a configured writer room of the selected provider. Ollama rooms are
        // GPU-scheduled (priority -> affinity -> FIFO) and only reload the model when
        // switching engines; OpenAI rooms book immediately. The wait for a free room is the
        // scheduler's job now — no busy-wait here.
        var lease = await studios.AcquireForGpuJobAsync(StudioKind.WriterRoom, preferredProvider, label, ct);

        if (lease is null && preferOpenAi)
        {
            // OpenAI selected but no OpenAI room: use the settings key if present, else fall
            // back to a local Ollama room/endpoint.
            if (!string.IsNullOrWhiteSpace(settings.OpenAiApiKey))
            {
                return await CompleteWithSettingsOpenAiAsync(settings, generation, label, ct);
            }

            logger.LogWarning(
                "Writer Room OpenAI selected but no API key is configured; falling back to Ollama model {Model}",
                llmOptions.Value.Model);
            lease = await studios.AcquireForGpuJobAsync(StudioKind.WriterRoom, StudioProviders.Ollama, label, ct);
        }

        if (lease is not null)
        {
            var success = false;
            try
            {
                var result = await CompleteWithWriterRoomAsync(lease.Studio, settings, generation, label, ct);
                success = true;
                return result;
            }
            finally
            {
                await lease.CompleteAsync(success, CancellationToken.None);
            }
        }

        // No configured writer room at all — the default Ollama endpoint (also GPU-scheduled).
        return await CompleteWithDefaultOllamaAsync(generation, label, ct);
    }

    private async Task<string> CompleteWithSettingsOpenAiAsync(
        StationSettings settings, TextGenerationRequest generation, string label, CancellationToken ct)
    {
        logger.LogInformation("Writer Room provider selected: OpenAI model {Model}", settings.OpenAiModel);
        return await CompleteWithHistoryAsync(
            studioId: null,
            studioName: "Writer Room (OpenAI settings)",
            provider: StudioProviders.OpenAi,
            model: settings.OpenAiModel,
            endpoint: "https://api.openai.com",
            label,
            generation.SystemPrompt,
            generation.UserPrompt,
            async token =>
            {
                var openAi = new OpenAiTextGenerationService(
                    httpClientFactory.CreateClient(OpenAiClientName), settings.OpenAiApiKey, settings.OpenAiModel);
                return await openAi.CompleteAsync(generation, token);
            },
            ct);
    }

    private async Task<string> CompleteWithDefaultOllamaAsync(
        TextGenerationRequest generation, string label, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(OllamaClientName);
        var endpoint = client.BaseAddress?.ToString();

        // Hold the GPU turn (priority -> affinity -> FIFO; unload only on engine switch) around
        // the whole completion.
        await using var turn = await studios.AcquireGpuTurnAsync(StudioKind.WriterRoom, endpoint, label, ct);
        return await CompleteWithHistoryAsync(
            studioId: null,
            studioName: "Writer Room (Ollama default)",
            provider: StudioProviders.Ollama,
            model: llmOptions.Value.Model,
            endpoint: endpoint,
            label,
            generation.SystemPrompt,
            generation.UserPrompt,
            async token =>
            {
                var ollama = new OllamaTextGenerationService(
                    client,
                    llmOptions,
                    loggerFactory.CreateLogger<OllamaTextGenerationService>());
                return await ollama.CompleteAsync(generation, token);
            },
            ct);
    }

    private async Task<string> CompleteWithWriterRoomAsync(
        Studio writerRoom,
        StationSettings settings,
        TextGenerationRequest generation,
        string label,
        CancellationToken ct)
    {
        var systemPrompt = generation.SystemPrompt;
        var userPrompt = generation.UserPrompt;
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
                    return await openAi.CompleteAsync(generation, token);
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
                return await ollama.CompleteAsync(generation, token);
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

using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
using WhipRadio.Core.Helpers;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed partial class ChatActionExecutor
{
    private async Task<ChatActionRecord> ExecuteSetProductionSwitchAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        string switchName = Require(call, "switch").Trim().ToLowerInvariant();
        bool enabled = ParseBool(Require(call, "enabled"));
        string reason = Require(call, "reason");

        if (switchName is not ("musicproduction" or "playout" or "news" or "weather" or "greetings"))
        {
            return Failed(call, "switch must be one of: musicProduction, playout, news, weather, greetings.");
        }

        // Taking the station off air is the one switch that needs Boss confirmation.
        if (switchName == "playout" && !enabled
            && await GateAsync(call, context, ApprovalRisk.Settings, $"Turn playout OFF ({reason})", ct)
                is { } queued)
        {
            return queued;
        }

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        StationSettings settings = await LoadOrCreateSettingsAsync(db, ct);
        switch (switchName)
        {
            case "musicproduction": settings.MusicProductionEnabled = enabled; break;
            case "playout": settings.PlayoutEnabled = enabled; break;
            case "news": settings.NewsEnabled = enabled; break;
            case "weather": settings.WeatherEnabled = enabled; break;
            case "greetings": settings.GreetingsEnabled = enabled; break;
        }

        await db.SaveChangesAsync(ct);
        if (switchName == "playout")
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<StationStatusReporter>().SetPlayoutEnabled(enabled);
        }

        return Succeeded(call, $"{switchName} is now {(enabled ? "on" : "off")}.");
    }

    private async Task<ChatActionRecord> ExecuteSetStationSettingsAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        string reason = Require(call, "reason");
        if (!TryParseSettings(Require(call, "settingsJson"), out Dictionary<string, JsonElement> fields, out string? error))
        {
            return Failed(call, error!);
        }

        if (await GateAsync(call, context, ApprovalRisk.Settings, $"Change station settings ({reason})", ct)
            is { } queued)
        {
            return queued;
        }

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        StationSettings settings = await LoadOrCreateSettingsAsync(db, ct);
        List<string> changed = [];
        ApplyString(fields, "stationName", value => { settings.StationName = value; changed.Add("name"); });
        ApplyString(fields, "stationSlogan", value => { settings.StationSlogan = value; changed.Add("slogan"); });
        ApplyString(fields, "stationVision", value => { settings.StationVision = value; changed.Add("vision"); });
        ApplyString(fields, "stationMission", value => { settings.StationMission = value; changed.Add("mission"); });
        ApplyString(fields, "defaultLanguage", value => { settings.DefaultLanguage = value; changed.Add("language"); });
        ApplyInt(fields, "targetQueueLength", 1, 200, value => { settings.TargetQueueLength = value; changed.Add("queueLength"); });
        if (changed.Count == 0)
        {
            return Failed(call, "No editable station settings were provided (name, slogan, vision, mission, defaultLanguage, targetQueueLength).");
        }

        await db.SaveChangesAsync(ct);
        return Succeeded(call, $"Updated station settings: {string.Join(", ", changed)}.");
    }

    private async Task<ChatActionRecord> ExecuteSetProviderSettingsAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        string providerArea = Require(call, "providerArea").Trim().ToLowerInvariant();
        string reason = Require(call, "reason");
        if (!TryParseSettings(Require(call, "settingsJson"), out Dictionary<string, JsonElement> fields, out string? error))
        {
            return Failed(call, error!);
        }

        if (await GateAsync(call, context, ApprovalRisk.Settings, $"Change {providerArea} provider settings ({reason})", ct)
            is { } queued)
        {
            return queued;
        }

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        StationSettings settings = await LoadOrCreateSettingsAsync(db, ct);
        List<string> changed = [];
        switch (providerArea)
        {
            case "text":
                ApplyString(fields, "textProvider", value => { settings.TextProvider = value; changed.Add("textProvider"); });
                ApplyString(fields, "openAiModel", value => { settings.OpenAiModel = value; changed.Add("openAiModel"); });
                break;
            case "music":
                ApplyString(fields, "defaultMusicProvider", value => { settings.DefaultMusicProvider = value; changed.Add("musicProvider"); });
                break;
            default:
                return Failed(call, "providerArea must be 'text' or 'music' (non-secret fields only).");
        }

        if (changed.Count == 0)
        {
            return Failed(call, "No editable provider settings were provided. Secrets and API keys are never accepted here.");
        }

        await db.SaveChangesAsync(ct);
        return Succeeded(call, $"Updated {providerArea} provider settings: {string.Join(", ", changed)}.");
    }

    private async Task<ChatActionRecord> ExecuteSetNewsProductionSettingsAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        string reason = Require(call, "reason");
        if (!TryParseSettings(Require(call, "settingsJson"), out Dictionary<string, JsonElement> fields, out string? error))
        {
            return Failed(call, error!);
        }

        if (await GateAsync(call, context, ApprovalRisk.Settings, $"Change news production settings ({reason})", ct)
            is { } queued)
        {
            return queued;
        }

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        StationSettings settings = await LoadOrCreateSettingsAsync(db, ct);
        List<string> changed = [];
        ApplyBool(fields, "newsEnabled", value => { settings.NewsEnabled = value; changed.Add("enabled"); });
        ApplyBool(fields, "newsLongFormatEnabled", value => { settings.NewsLongFormatEnabled = value; changed.Add("longFormat"); });
        ApplyInt(fields, "cadenceMinutes", 15, 240, value => { settings.NewsPackageCadenceMinutes = value; changed.Add("cadence"); });
        ApplyInt(fields, "maxDurationSeconds", 60, 1800, value => { settings.NewsPackageMaxDurationSeconds = value; changed.Add("maxDuration"); });
        if (changed.Count == 0)
        {
            return Failed(call, "No editable news settings were provided (newsEnabled, newsLongFormatEnabled, cadenceMinutes, maxDurationSeconds).");
        }

        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishNewsChangedAsync(ct);
        return Succeeded(call, $"Updated news production settings: {string.Join(", ", changed)}.");
    }

    private async Task<ChatActionRecord> ExecuteSetWeatherSettingsAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        string reason = Require(call, "reason");
        if (!TryParseSettings(Require(call, "settingsJson"), out Dictionary<string, JsonElement> fields, out string? error))
        {
            return Failed(call, error!);
        }

        // A location change alters on-air facts and needs Boss confirmation.
        bool changesLocation = fields.ContainsKey("weatherLocationName")
            || fields.ContainsKey("weatherLatitude")
            || fields.ContainsKey("weatherLongitude");
        if (changesLocation
            && await GateAsync(call, context, ApprovalRisk.Settings, $"Change weather location ({reason})", ct)
                is { } queued)
        {
            return queued;
        }

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        StationSettings settings = await LoadOrCreateSettingsAsync(db, ct);
        List<string> changed = [];
        ApplyBool(fields, "weatherEnabled", value => { settings.WeatherEnabled = value; changed.Add("enabled"); });
        ApplyBool(fields, "weatherFullHandoverEnabled", value => { settings.WeatherFullHandoverEnabled = value; changed.Add("fullHandover"); });
        ApplyInt(fields, "cadenceMinutes", 15, 240, value => { settings.WeatherCadenceMinutes = value; changed.Add("cadence"); });
        ApplyString(fields, "weatherLocationName", value => { settings.WeatherLocationName = value; changed.Add("location"); });
        ApplyDouble(fields, "weatherLatitude", -90, 90, value => { settings.WeatherLatitude = value; changed.Add("lat"); });
        ApplyDouble(fields, "weatherLongitude", -180, 180, value => { settings.WeatherLongitude = value; changed.Add("lon"); });
        if (changed.Count == 0)
        {
            return Failed(call, "No editable weather settings were provided.");
        }

        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishWeatherChangedAsync(ct);
        return Succeeded(call, $"Updated weather settings: {string.Join(", ", changed)}.");
    }

    private async Task<ChatActionRecord> ExecuteManageNewsFeedAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        string operation = Require(call, "operation").Trim().ToLowerInvariant();
        string reason = Require(call, "reason");

        if (operation == "toggle")
        {
            // Enable/disable is reversible and does not need confirmation.
            return await ToggleNewsFeedAsync(call, ct);
        }

        if (operation is not ("add" or "update" or "delete"))
        {
            return Failed(call, "operation must be add, update, toggle, or delete.");
        }

        if (await GateAsync(call, context, ApprovalRisk.External, $"News feed {operation} ({reason})", ct)
            is { } queued)
        {
            return queued;
        }

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        if (operation == "delete")
        {
            if (!Guid.TryParse(Require(call, "feedId"), out Guid deleteId))
            {
                return Failed(call, "feedId must be a valid id for delete.");
            }

            int removed = await db.NewsFeeds.Where(f => f.Id == deleteId).ExecuteDeleteAsync(ct);
            await productionUpdates.PublishNewsChangedAsync(ct);
            return removed > 0 ? Succeeded(call, "News feed deleted.") : Failed(call, "That news feed was not found.");
        }

        string url = Require(call, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            return Failed(call, "url must be an absolute http or https URL.");
        }

        string label = Optional(call, "label") ?? uri.Host;
        if (operation == "add")
        {
            if (await db.NewsFeeds.AnyAsync(f => f.Url == uri.ToString(), ct))
            {
                return Failed(call, "A feed with that URL already exists.");
            }

            db.NewsFeeds.Add(new NewsFeed
            {
                Id = Guid.NewGuid(),
                Label = label.Trim(),
                Url = uri.ToString(),
                Language = (Optional(call, "language") ?? "en").ToLowerInvariant(),
                Region = (Optional(call, "region") ?? "global").ToLowerInvariant(),
                Category = (Optional(call, "category") ?? "general").ToLowerInvariant(),
                IsEnabled = true,
                CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync(ct);
            await productionUpdates.PublishNewsChangedAsync(ct);
            return Succeeded(call, $"Added news feed '{label}'.");
        }

        if (!Guid.TryParse(Require(call, "feedId"), out Guid updateId))
        {
            return Failed(call, "feedId must be a valid id for update.");
        }

        NewsFeed? feed = await db.NewsFeeds.FirstOrDefaultAsync(f => f.Id == updateId, ct);
        if (feed is null)
        {
            return Failed(call, "That news feed was not found.");
        }

        feed.Label = label.Trim();
        feed.Url = uri.ToString();
        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishNewsChangedAsync(ct);
        return Succeeded(call, $"Updated news feed '{feed.Label}'.");
    }

    private async Task<ChatActionRecord> ToggleNewsFeedAsync(CharacterToolCall call, CancellationToken ct)
    {
        if (!Guid.TryParse(Require(call, "feedId"), out Guid feedId))
        {
            return Failed(call, "feedId must be a valid id to toggle.");
        }

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        NewsFeed? feed = await db.NewsFeeds.FirstOrDefaultAsync(f => f.Id == feedId, ct);
        if (feed is null)
        {
            return Failed(call, "That news feed was not found.");
        }

        feed.IsEnabled = !feed.IsEnabled;
        await db.SaveChangesAsync(ct);
        await productionUpdates.PublishNewsChangedAsync(ct);
        return Succeeded(call, $"News feed '{feed.Label}' is now {(feed.IsEnabled ? "enabled" : "disabled")}.");
    }

    private async Task<ChatActionRecord> ExecuteAnswerListenerMessageAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        if (!Guid.TryParse(Require(call, "messageId"), out Guid messageId))
        {
            return Failed(call, "messageId must be a valid id.");
        }

        string action = Require(call, "action").Trim().ToLowerInvariant();
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        ListenerMessage? message = await db.ListenerMessages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null)
        {
            return Failed(call, "That listener message was not found.");
        }

        // A host may only handle its own or unassigned current-show messages.
        if (context.SenderModerator is { } host && message.ModeratorId is { } owner && owner != host.Id)
        {
            return Failed(call, "That listener message is assigned to another host.");
        }

        switch (action)
        {
            case "dismiss":
                message.Status = ListenerMessageStatus.Dismissed;
                message.DismissalReason = Optional(call, "reason") ?? "Dismissed by host.";
                await db.SaveChangesAsync(ct);
                await hub.Clients.All.SendAsync("ListenerMessagesChanged", ct);
                return Succeeded(call, "Listener message dismissed.");

            case "queue_greeting":
            case "queue_dedication":
                if (context.SenderModerator is { } presenter)
                {
                    message.ModeratorId = presenter.Id;
                }

                message.Status = ListenerMessageStatus.Queued;
                await db.SaveChangesAsync(ct);
                await hub.Clients.All.SendAsync("ListenerMessagesChanged", ct);
                return Succeeded(call, "Listener message queued to air in the next break.");

            case "reply_in_chat":
                return Succeeded(call, "Acknowledged the listener message; reply in the chat as usual.");

            default:
                return Failed(call, "action must be queue_greeting, queue_dedication, dismiss, or reply_in_chat.");
        }
    }

    private async Task<ChatActionRecord> ExecuteEmergencyAnnouncementAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        string text = Require(call, "text");
        TalkBreakPriority priority = string.Equals(Optional(call, "priority"), "emergency", StringComparison.OrdinalIgnoreCase)
            ? TalkBreakPriority.Emergency
            : TalkBreakPriority.High;

        // Resolve the voice: explicit moderator, the current sender host, or the on-air host.
        Moderator moderator;
        string? moderatorArg = Optional(call, "moderator");
        if (!string.IsNullOrWhiteSpace(moderatorArg))
        {
            moderator = await director.ResolveHostAsync(moderatorArg, ct);
        }
        else if (context.SenderModerator is { } sender)
        {
            moderator = sender;
        }
        else
        {
            moderator = (await schedule.GetCurrentAsync(ct)).Moderator;
        }

        // Emergency priority is Boss-confirmed unless the Boss triggered it directly here.
        bool bossTriggered = context.Channel.Kind is ChatChannelKind.DirectorDm or ChatChannelKind.Station;
        if (priority == TalkBreakPriority.Emergency && !bossTriggered
            && await GateAsync(call, context, ApprovalRisk.External, $"Emergency announcement: {Trim(text, 80)}", ct)
                is { } queued)
        {
            return queued;
        }

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        StationSettings settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);
        ProduceEmergencyInBackgroundAsync(moderator, text, priority, settings.StationName).Forget(logger);
        return Succeeded(call, $"Emergency message for {moderator.Name} is being produced and will jump to the front of playout.");
    }

    private async Task ProduceEmergencyInBackgroundAsync(
        Moderator moderator,
        string text,
        TalkBreakPriority priority,
        string stationName)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AnnouncementFactory factory = scope.ServiceProvider.GetRequiredService<AnnouncementFactory>();
            Announcement announcement = await factory.ProduceAsync(
                AnnouncementKind.Banter,
                moderator,
                relatedTrack: null,
                facts: $"URGENT station announcement. Deliver this message clearly and calmly: {text}",
                stationName,
                CancellationToken.None,
                purpose: "emergency announcement");
            await PromoteTalkBreakAsync(announcement.Id, priority);
            await priorityDispatcher.PushReadyAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Emergency announcement for {Host} failed", moderator.Name);
            await notifications.PublishAsync(new StationNotification(
                "Failure",
                "chat:EmergencyAnnouncement",
                $"Emergency announcement for {moderator.Name} failed: {ex.GetBaseException().Message}",
                timeProvider.GetUtcNow().UtcDateTime));
        }
    }

    private static async Task<StationSettings> LoadOrCreateSettingsAsync(RadioDbContext db, CancellationToken ct)
    {
        StationSettings? settings = await db.StationSettings.FindStationSettingsAsync(ct);
        if (settings is null)
        {
            settings = new StationSettings { Id = StationSettings.SingletonId };
            db.StationSettings.Add(settings);
        }

        return settings;
    }

    private static bool TryParseSettings(
        string json,
        out Dictionary<string, JsonElement> fields,
        out string? error)
    {
        fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        error = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "settingsJson must be a JSON object.";
                return false;
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                fields[property.Name] = property.Value.Clone();
            }

            return true;
        }
        catch (JsonException)
        {
            error = "settingsJson is not valid JSON.";
            return false;
        }
    }

    private static void ApplyString(Dictionary<string, JsonElement> fields, string key, Action<string> apply)
    {
        if (fields.TryGetValue(key, out JsonElement value) && value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString()!.Trim();
            if (text.Length > 0)
            {
                apply(text);
            }
        }
    }

    private static void ApplyBool(Dictionary<string, JsonElement> fields, string key, Action<bool> apply)
    {
        if (fields.TryGetValue(key, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            apply(value.GetBoolean());
        }
    }

    private static void ApplyInt(Dictionary<string, JsonElement> fields, string key, int min, int max, Action<int> apply)
    {
        if (fields.TryGetValue(key, out JsonElement value) && value.TryGetInt32(out int parsed))
        {
            apply(Math.Clamp(parsed, min, max));
        }
    }

    private static void ApplyDouble(Dictionary<string, JsonElement> fields, string key, double min, double max, Action<double> apply)
    {
        if (fields.TryGetValue(key, out JsonElement value) && value.TryGetDouble(out double parsed))
        {
            apply(Math.Clamp(parsed, min, max));
        }
    }

    private static string Trim(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}

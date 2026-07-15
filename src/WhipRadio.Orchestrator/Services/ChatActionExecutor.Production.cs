using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Helpers;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;

namespace WhipRadio.Orchestrator.Services;

public sealed partial class ChatActionExecutor
{
    private async Task<ChatActionRecord> ExecuteProduceNewsPackageAsync(CharacterToolCall call, CancellationToken ct)
    {
        string mode = (Optional(call, "mode") ?? "next").Trim().ToLowerInvariant();
        if (mode == "recreate")
        {
            string packageArg = Require(call, "packageId");
            if (!Guid.TryParse(packageArg, out Guid packageId))
            {
                return Failed(call, "packageId must be a valid package id.");
            }

            ProduceNewsInBackgroundAsync(packageId).Forget();
            return Succeeded(call, "Recreating that news package now; it will appear when ready.");
        }

        ProduceNewsInBackgroundAsync(packageId: null).Forget();
        return Succeeded(call, "Producing the next news package now; it will appear when ready.");
    }

    private async Task ProduceNewsInBackgroundAsync(Guid? packageId)
    {
        try
        {
            NewsPackage? package = packageId is { } id
                ? await newsProduction.RecreatePackageAsync(id, CancellationToken.None)
                : await newsProduction.ProduceNextPackageAsync(CancellationToken.None);
            if (package is null)
            {
                await notifications.PublishAsync(new StationNotification(
                    "News",
                    "chat:ProduceNewsPackage",
                    "No news package was produced (no fresh items or no news presenter).",
                    timeProvider.GetUtcNow().UtcDateTime));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Chat-requested news package production failed");
            await notifications.PublishAsync(new StationNotification(
                "Failure",
                "chat:ProduceNewsPackage",
                $"News package production failed: {ex.GetBaseException().Message}",
                timeProvider.GetUtcNow().UtcDateTime));
        }
    }

    private async Task<ChatActionRecord> ExecuteProduceWeatherReportAsync(
        CharacterToolCall call,
        ChatActionContext context,
        CancellationToken ct)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        StationSettings settings = await db.StationSettings.AsNoTracking().GetStationSettingsOrDefaultAsync(ct);

        Moderator? presenter;
        if (context.SenderModerator is { IsWeatherSpecialist: true } specialist)
        {
            presenter = specialist;
        }
        else
        {
            string? presenterArg = Optional(call, "presenter");
            int? presenterId = null;
            if (!string.IsNullOrWhiteSpace(presenterArg))
            {
                presenterId = (await director.ResolveHostAsync(presenterArg, ct)).Id;
            }
            else if (settings.WeatherSpecialistModeratorId is { } configured)
            {
                presenterId = configured;
            }

            presenter = presenterId is { } id
                ? await db.Moderators.AsNoTracking().FirstOrDefaultAsync(host => host.Id == id && host.IsActive, ct)
                : null;
        }

        if (presenter is null)
        {
            return Failed(
                call,
                "No weather presenter is set. Name one with 'presenter', or hire a weather specialist first.");
        }

        if (!presenter.IsWeatherSpecialist)
        {
            return Failed(call, $"{presenter.Name} is not a weather specialist.");
        }

        ProduceWeatherInBackgroundAsync(presenter, settings.StationName).Forget();
        return Succeeded(
            call,
            $"Weather segment for {presenter.Name} is in production and will air in the next break.");
    }

    private async Task ProduceWeatherInBackgroundAsync(Moderator presenter, string stationName)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            IWeatherReportSource weatherSource = scope.ServiceProvider.GetRequiredService<IWeatherReportSource>();
            AnnouncementFactory factory = scope.ServiceProvider.GetRequiredService<AnnouncementFactory>();

            Core.Weather.WeatherReport report = await weatherSource.GetReportAsync(presenter.Language, CancellationToken.None);
            Announcement announcement = await factory.ProduceAsync(
                AnnouncementKind.Weather,
                presenter,
                relatedTrack: null,
                facts: report.ToFacts(timeProvider.GetLocalNow().DateTime),
                stationName,
                CancellationToken.None,
                purpose: "chat-requested weather");
            await PromoteTalkBreakAsync(announcement.Id, TalkBreakPriority.High);
            await priorityDispatcher.PushReadyAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Chat-requested weather segment for {Host} failed", presenter.Name);
            await notifications.PublishAsync(new StationNotification(
                "Failure",
                "chat:ProduceWeatherReport",
                $"Weather segment for {presenter.Name} failed: {ex.GetBaseException().Message}",
                timeProvider.GetUtcNow().UtcDateTime));
        }
    }

    /// <summary>Raises the announcement's talk break to a jump-the-line priority.</summary>
    private async Task PromoteTalkBreakAsync(Guid announcementId, TalkBreakPriority priority)
    {
        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        TalkBreak? talkBreak = await db.TalkBreaks
            .Include(item => item.Parts)
            .FirstOrDefaultAsync(item => item.AnnouncementId == announcementId);
        if (talkBreak is null)
        {
            return;
        }

        talkBreak.Priority = priority;
        talkBreak.ExpiresAtUtc = timeProvider.GetUtcNow().UtcDateTime.AddHours(24);
        foreach (TalkPart part in talkBreak.Parts)
        {
            part.Priority = priority;
            part.ExpiresAtUtc = talkBreak.ExpiresAtUtc;
        }

        await db.SaveChangesAsync();
    }

    private async Task<ChatActionRecord> ExecuteCreateJingleAsync(CharacterToolCall call, CancellationToken ct)
    {
        string label = Require(call, "label");
        string style = Require(call, "style");
        int duration = int.TryParse(Optional(call, "durationSeconds"), out int parsed)
            ? Math.Clamp(parsed, 3, 30)
            : 10;

        CreateJingleAsync(new CreateJingleDto(label, style, duration)).Forget();
        return Succeeded(call, $"Generating jingle '{label}' now; it will appear in Branding when ready.");
    }

    private async Task CreateJingleAsync(CreateJingleDto request)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            JingleProductionService production = scope.ServiceProvider.GetRequiredService<JingleProductionService>();
            await production.GenerateAsync(request, CancellationToken.None);
            await hub.Clients.All.SendAsync("JinglesChanged");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Chat-requested jingle '{Label}' failed", request.Label);
            await notifications.PublishAsync(new StationNotification(
                "Failure",
                "chat:CreateJingle",
                $"Jingle '{request.Label}' could not be generated: {ex.GetBaseException().Message}",
                timeProvider.GetUtcNow().UtcDateTime));
        }
    }

    private async Task<ChatActionRecord> ExecuteSetJingleActiveAsync(CharacterToolCall call, CancellationToken ct)
    {
        string value = Require(call, "jingle");
        bool isActive = ParseBool(Require(call, "isActive"));

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        Jingle? jingle = null;
        if (Guid.TryParse(value, out Guid id))
        {
            jingle = await db.Jingles.FirstOrDefaultAsync(item => item.Id == id, ct);
        }

        if (jingle is null)
        {
            string lowered = value.Trim().ToLower();
            List<Jingle> named = await db.Jingles.Where(item => item.Label.ToLower() == lowered).Take(2).ToListAsync(ct);
            if (named.Count == 0)
            {
                return Failed(call, $"No jingle labelled '{value}' was found.");
            }

            if (named.Count > 1)
            {
                return Failed(call, $"Several jingles are labelled '{value}'. Pass the exact id.");
            }

            jingle = named[0];
        }

        jingle.IsActive = isActive;
        await db.SaveChangesAsync(ct);
        await hub.Clients.All.SendAsync("JinglesChanged", ct);
        return Succeeded(call, $"Jingle '{jingle.Label}' is now {(isActive ? "active" : "inactive")}.");
    }

    private async Task<ChatActionRecord> ExecuteSetPresenterAsync(CharacterToolCall call, bool isNews, CancellationToken ct)
    {
        Moderator host = await director.ResolveHostAsync(Require(call, "host"), ct);
        if (isNews && !host.IsNewsSpecialist)
        {
            return Failed(call, $"{host.Name} is not a news specialist. Hire one or pick a news host.");
        }

        if (!isNews && !host.IsWeatherSpecialist)
        {
            return Failed(call, $"{host.Name} is not a weather specialist. Hire one or pick a weather host.");
        }

        await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
        StationSettings? settings = await db.StationSettings.FindStationSettingsAsync(ct);
        if (settings is null)
        {
            settings = new StationSettings { Id = StationSettings.SingletonId };
            db.StationSettings.Add(settings);
        }

        if (isNews)
        {
            settings.NewsPresenterModeratorId = host.Id;
        }
        else
        {
            settings.WeatherSpecialistModeratorId = host.Id;
        }

        await db.SaveChangesAsync(ct);
        if (isNews)
        {
            await productionUpdates.PublishNewsChangedAsync(ct);
        }
        else
        {
            await productionUpdates.PublishWeatherChangedAsync(ct);
        }

        return Succeeded(call, $"{host.Name} is now the {(isNews ? "news" : "weather")} presenter.");
    }

    private static bool ParseBool(string value)
        => value.Trim().ToLowerInvariant() is "true" or "yes" or "on" or "1" or "active";
}

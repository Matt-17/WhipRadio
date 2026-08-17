using Microsoft.EntityFrameworkCore;
using WhipRadio.Core.Api;
using WhipRadio.Core.Entities;
using WhipRadio.Core.Prompting;
using WhipRadio.Infrastructure.Persistence;
using WhipRadio.Orchestrator.Services;

namespace WhipRadio.Orchestrator.Api;

public static partial class RadioApiEndpoints
{
    // Roles a verb explorer can invoke as; kept in TOOLS.md category order for the page.
    private static readonly IReadOnlyDictionary<string, string> VerbCategories = new Dictionary<string, string>
    {
        ["Message"] = "Communication",
        ["RequestBossApproval"] = "Communication",
        ["SearchMusic"] = "Music & Library",
        ["SearchArtist"] = "Music & Library",
        ["GetArtistProfile"] = "Music & Library",
        ["QueueTrack"] = "Music & Library",
        ["RetireTrack"] = "Music & Library",
        ["RetireArtist"] = "Music & Library",
        ["DeleteTrack"] = "Music & Library",
        ["DeleteArtist"] = "Music & Library",
        ["RedefineArtistProfile"] = "Music & Library",
        ["MakeSong"] = "Music & Library",
        ["RequestSongFromArtist"] = "Music & Library",
        ["PostArtistFeed"] = "Music & Library",
        ["CancelSongProduction"] = "Music & Library",
        ["LookupKnowledge"] = "Music & Library",
        ["Announcement"] = "On-Air & Production",
        ["EmergencyAnnouncement"] = "On-Air & Production",
        ["PlanTalkBreak"] = "On-Air & Production",
        ["CreateTalkBit"] = "On-Air & Production",
        ["BriefPodcast"] = "On-Air & Production",
        ["ProduceNewsPackage"] = "On-Air & Production",
        ["ProduceWeatherReport"] = "On-Air & Production",
        ["AnswerListenerMessage"] = "On-Air & Production",
        ["Remember"] = "On-Air & Production",
        ["PlanFormat"] = "Director",
        ["RemoveShow"] = "Director",
        ["AssignHost"] = "Director",
        ["HireHost"] = "Director",
        ["FireHost"] = "Director",
        ["Invite"] = "Director",
        ["RemoveFromChannel"] = "Director",
        ["StatusReport"] = "Director",
        ["CreateJingle"] = "Branding, News & Weather",
        ["SetJingleActive"] = "Branding, News & Weather",
        ["DeleteJingle"] = "Branding, News & Weather",
        ["SetNewsPresenter"] = "Branding, News & Weather",
        ["SetWeatherPresenter"] = "Branding, News & Weather",
        ["ManageNewsFeed"] = "Branding, News & Weather",
        ["SetNewsProductionSettings"] = "Branding, News & Weather",
        ["SetWeatherSettings"] = "Branding, News & Weather",
        ["StudioStatus"] = "Operations & Diagnostics",
        ["ServerStatus"] = "Operations & Diagnostics",
        ["PrivacyReport"] = "Operations & Diagnostics",
        ["MediaCleanupPreview"] = "Operations & Diagnostics",
        ["RunMediaCleanup"] = "Operations & Diagnostics",
        ["SetStationSettings"] = "Operations & Diagnostics",
        ["SetProductionSwitch"] = "Operations & Diagnostics",
        ["SetProviderSettings"] = "Operations & Diagnostics",
    };

    private static readonly HashSet<string> DestructiveVerbs =
    [
        "DeleteTrack", "DeleteArtist", "DeleteJingle", "RemoveShow", "FireHost",
        "RunMediaCleanup", "RetireTrack", "RetireArtist", "CancelSongProduction",
    ];

    private static readonly HashSet<string> ApprovalGatedVerbs =
    [
        "DeleteTrack", "DeleteArtist", "DeleteJingle", "RemoveShow", "FireHost",
        "RunMediaCleanup", "RedefineArtistProfile", "SetStationSettings",
        "SetProductionSwitch", "SetProviderSettings", "SetNewsProductionSettings",
        "SetWeatherSettings", "ManageNewsFeed", "CancelSongProduction", "EmergencyAnnouncement",
    ];

    private static readonly HashSet<string> BackgroundVerbs =
    [
        "Announcement", "EmergencyAnnouncement", "MakeSong", "BriefPodcast", "CreateJingle",
        "HireHost", "ProduceNewsPackage", "ProduceWeatherReport", "SearchArtist",
        "RedefineArtistProfile", "RequestSongFromArtist",
    ];

    private static readonly CharacterRole[] AllRoles =
    [
        CharacterRole.ProgramDirector, CharacterRole.Host, CharacterRole.NewsSpecialist,
        CharacterRole.WeatherSpecialist, CharacterRole.Artist,
    ];

    private static void MapVerbs(RouteGroupBuilder api)
    {
        RouteGroupBuilder verbs = api.MapGroup("/verbs");

        verbs.MapGet("", (ICharacterToolCatalog catalog) =>
        {
            // Union every Chat-scope tool across all roles, recording which roles each is offered to.
            Dictionary<string, (CharacterToolDefinition Def, List<string> Roles)> map = new();
            foreach (CharacterRole role in AllRoles)
            {
                foreach (CharacterToolDefinition def in catalog.GetTools(PromptScope.Chat, role))
                {
                    if (!map.TryGetValue(def.Name, out var entry))
                    {
                        entry = (def, []);
                        map[def.Name] = entry;
                    }

                    entry.Roles.Add(role.ToString());
                }
            }

            List<VerbDefinitionDto> result = map.Values
                .Select(entry => new VerbDefinitionDto(
                    entry.Def.Name,
                    entry.Def.Description,
                    VerbCategories.GetValueOrDefault(entry.Def.Name, "Other"),
                    entry.Def.Arguments.Select(arg => new VerbArgumentDto(arg.Name, arg.Description, arg.IsRequired)).ToList(),
                    entry.Roles,
                    DestructiveVerbs.Contains(entry.Def.Name),
                    ApprovalGatedVerbs.Contains(entry.Def.Name),
                    BackgroundVerbs.Contains(entry.Def.Name)))
                .OrderBy(dto => dto.Category)
                .ThenBy(dto => dto.Name)
                .ToList();
            return Results.Ok(result);
        });

        verbs.MapPost("/invoke", async (
            InvokeVerbRequest request,
            ChatActionExecutor executor,
            ChatService chat,
            ChatParticipantResolver participants,
            IDbContextFactory<RadioDbContext> dbFactory,
            CancellationToken ct) =>
        {
            (ChatParticipant? sender, Guid channelId, string? error) = await ResolveInvokeSenderAsync(
                request.Role, request.ActorId, chat, participants, ct);
            if (sender is null)
            {
                return Results.BadRequest(error);
            }

            await using RadioDbContext db = await dbFactory.CreateDbContextAsync(ct);
            ChatChannel? channel = await db.ChatChannels.AsNoTracking()
                .Include(c => c.Moderator)
                .Include(c => c.CounterpartModerator)
                .FirstOrDefaultAsync(c => c.Id == channelId, ct);
            if (channel is null)
            {
                return Results.BadRequest("Could not resolve a chat channel for the chosen role.");
            }

            ChatActionContext context = new(channel, null, sender, Guid.NewGuid(), 0);
            CharacterToolCall call = new(
                request.Name,
                request.Arguments as IReadOnlyDictionary<string, string>
                    ?? new Dictionary<string, string>(request.Arguments));
            ChatActionRecord record = await executor.ExecuteAsync(call, context, ct);
            return Results.Ok(new InvokeVerbResultDto(record.State.ToString(), record.ResultSummary));
        });
    }

    private static async Task<(ChatParticipant? Sender, Guid ChannelId, string? Error)> ResolveInvokeSenderAsync(
        string role,
        string? actorId,
        ChatService chat,
        ChatParticipantResolver participants,
        CancellationToken ct)
    {
        switch (role.Trim().ToLowerInvariant())
        {
            case "programdirector":
            case "director":
                return (ChatParticipantResolver.Director, await chat.GetDirectorChannelIdAsync(ct), null);

            case "host":
            case "newsspecialist":
            case "weatherspecialist":
            {
                if (!int.TryParse(actorId, out int moderatorId))
                {
                    return (null, Guid.Empty, "A host actor id is required for host roles.");
                }

                ChatParticipant? host = await participants.ResolveAsync(ChatParticipantRef.ForHost(moderatorId), ct);
                if (host is null)
                {
                    return (null, Guid.Empty, "That host was not found or is inactive.");
                }

                Guid channelId = await chat.GetHostDmChannelIdAsync(moderatorId, ct)
                    ?? await chat.GetDirectorChannelIdAsync(ct);
                return (host, channelId, null);
            }

            case "artist":
            {
                if (!Guid.TryParse(actorId, out Guid memberId))
                {
                    return (null, Guid.Empty, "An artist member id is required for the artist role.");
                }

                ChatParticipant? artist = await participants.ResolveAsync(ChatParticipantRef.ForArtistMember(memberId), ct);
                if (artist is null)
                {
                    return (null, Guid.Empty, "That artist member was not found.");
                }

                return (artist, await chat.GetStationChannelIdAsync(ct), null);
            }

            default:
                return (null, Guid.Empty, $"Unknown role '{role}'.");
        }
    }

    private static void MapApprovals(RouteGroupBuilder api)
    {
        RouteGroupBuilder approvals = api.MapGroup("/approvals");

        approvals.MapGet("", async (ApprovalService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(ct)));

        approvals.MapPost("/{id:guid}/approve", async (Guid id, ApprovalService service, CancellationToken ct) =>
        {
            (bool ok, string message) = await service.ApproveAsync(id, ct);
            return ok ? Results.Ok(message) : Results.BadRequest(message);
        });

        approvals.MapPost("/{id:guid}/deny", async (Guid id, DenyApprovalRequest? request, ApprovalService service, CancellationToken ct) =>
        {
            (bool ok, string message) = await service.DenyAsync(id, request?.Reason, ct);
            return ok ? Results.Ok(message) : Results.BadRequest(message);
        });
    }
}

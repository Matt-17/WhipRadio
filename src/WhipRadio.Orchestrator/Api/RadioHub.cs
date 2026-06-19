using Microsoft.AspNetCore.SignalR;

namespace WhipRadio.Orchestrator.Api;

/// <summary>
/// Push channel for the web app. Server-to-client events:
/// "NowPlayingChanged" (NowPlayingDto?), "VotesChanged" (VoteResultDto),
/// "QueueChanged" (List&lt;QueueItemDto&gt;), "JinglesChanged",
/// "ConsoleLineAdded" (ConsoleLineDto), "StudiosChanged",
/// "MixerChanged" (MixerOverviewDto).
/// </summary>
public class RadioHub : Hub;

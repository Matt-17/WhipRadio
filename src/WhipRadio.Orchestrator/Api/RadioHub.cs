using Microsoft.AspNetCore.SignalR;

namespace WhipRadio.Orchestrator.Api;

/// <summary>
/// Push channel for the web app. Server-to-client events:
/// "NowPlayingChanged" (NowPlayingDto?), "VotesChanged" (VoteResultDto),
/// "QueueChanged" (List&lt;QueueItemDto&gt;), "ConsoleLineAdded" (ConsoleLineDto).
/// </summary>
public class RadioHub : Hub;

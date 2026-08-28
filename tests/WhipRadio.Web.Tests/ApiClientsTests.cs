using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WhipRadio.Core.Api;
using WhipRadio.TestSupport;
using WhipRadio.Web.Services.Api;

namespace WhipRadio.Web.Tests;

/// <summary>
/// Characterization of the typed orchestrator API client conventions across
/// the feature-scoped clients, one
/// representative method per feature area: SafeGet null/empty fallbacks, the
/// (Dto?, string?) mutation tuples, error-body single-lining, and the
/// "Orchestrator not reachable." degradation.
/// </summary>
[TestClass]
public class ApiClientsTests
{
    // --- GET conventions ---------------------------------------------------------

    [TestMethod]
    public async Task Get_ListEndpoints_FallBackToEmptyLists_WhenUnreachable()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("down"));

        Assert.Empty(await CreateClient<StationApiClient>(handler).GetQueueAsync());
        Assert.Empty(await CreateClient<LibraryApiClient>(handler).GetArtistsAsync());
        Assert.Empty(await CreateClient<ChatApiClient>(handler).GetChatChannelsAsync());
        Assert.Empty(await CreateClient<ModeratorsApiClient>(handler).GetModeratorsAsync());
        Assert.Empty(await CreateClient<ArchiveApiClient>(handler).GetArchiveAsync());
        Assert.Empty(await CreateClient<SettingsApiClient>(handler).GetFormatsAsync());
    }

    [TestMethod]
    public async Task Get_SingleDtoEndpoints_FallBackToNull_WhenUnreachable()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("down"));
        var station = CreateClient<StationApiClient>(handler);
        var settings = CreateClient<SettingsApiClient>(handler);

        Assert.Null(await station.GetNowPlayingAsync());
        Assert.Null(await station.GetStationStatusAsync());
        Assert.Null(await settings.GetSettingsAsync());
        Assert.Null(await settings.GetMixerAsync());
        Assert.Null(await station.GetServerStatsAsync());
    }

    [TestMethod]
    public async Task GetLibrary_BuildsTheQueryString_AndParsesTheList()
    {
        var artistId = Guid.NewGuid();
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new[]
            {
                new { id = Guid.NewGuid(), title = "Song", artistName = "Band", genre = "House" },
            }),
        });
        var client = CreateClient<LibraryApiClient>(handler);

        var tracks = await client.GetLibraryAsync(sort: "newest", genre: "House & Techno", artistId: artistId);

        Assert.Equal(1, tracks.Count);
        Assert.Equal("Song", tracks[0].Title);
        Assert.Equal(
            $"/api/library?sort=newest&genre=House%20%26%20Techno&artistId={artistId}",
            handler.LastRequest!.RequestUri!.PathAndQuery);
    }

    [TestMethod]
    public async Task GetStudioOverview_FallsBackToAnEmptyOverview()
    {
        var client = CreateClient<StudiosApiClient>(new FakeHttpMessageHandler(_ => throw new HttpRequestException("down")));

        var overview = await client.GetStudioOverviewAsync();

        Assert.Empty(overview.Studios);
        Assert.Empty(overview.PendingOperations);
    }

    // --- mutation tuple conventions ------------------------------------------------

    [TestMethod]
    public async Task PostChatMessage_ReturnsTheDto_OnSuccess()
    {
        var channelId = Guid.NewGuid();
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { id = Guid.NewGuid(), text = "hello", senderName = "Boss" }),
        });
        var client = CreateClient<ChatApiClient>(handler);

        var (message, error) = await client.PostChatMessageAsync(channelId, "hello");

        Assert.Null(error);
        Assert.Equal("hello", message!.Text);
        Assert.Equal($"/api/chat/channels/{channelId}/messages", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("hello", handler.LastRequestBody);
    }

    [TestMethod]
    public async Task PostChatMessage_ReturnsTheErrorBody_OnFailure()
    {
        var handler = FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.BadRequest, new StringContent("channel is archived"));
        var client = CreateClient<ChatApiClient>(handler);

        var (message, error) = await client.PostChatMessageAsync(Guid.NewGuid(), "hello");

        Assert.Null(message);
        Assert.Equal("channel is archived", error);
    }

    [TestMethod]
    public async Task CreateArtist_UsesTheLongClient_AndDegradesWhenUnreachable()
    {
        var longHandler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { id = Guid.NewGuid(), name = "The Nightdrivers" }),
        });
        var shortHandler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("wrong client"));
        var client = CreateClient<LibraryApiClient>(shortHandler, longHandler);

        var (artist, error) = await client.CreateArtistAsync("synthwave duo");
        Assert.Null(error);
        Assert.Equal("The Nightdrivers", artist!.Name);
        Assert.Equal("/api/artists", longHandler.LastRequest!.RequestUri!.AbsolutePath);

        var deadClient = CreateClient<LibraryApiClient>(
            shortHandler, new FakeHttpMessageHandler(_ => throw new HttpRequestException("down")));
        var (noArtist, timeoutError) = await deadClient.CreateArtistAsync("hint");
        Assert.Null(noArtist);
        Assert.Equal("Artist creation timed out or the writer room is unreachable.", timeoutError);
    }

    [TestMethod]
    public async Task DeleteTrack_MapsStatusCodes_ToDeletedDeferredOrError()
    {
        var id = Guid.NewGuid();

        var deleted = await CreateClient<LibraryApiClient>(FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.NoContent, new StringContent(""))).DeleteTrackAsync(id);
        Assert.True(deleted.Deleted);
        Assert.False(deleted.Deferred);

        var deferred = await CreateClient<LibraryApiClient>(FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.Accepted, new StringContent(""))).DeleteTrackAsync(id);
        Assert.False(deferred.Deleted);
        Assert.True(deferred.Deferred);

        var failed = await CreateClient<LibraryApiClient>(FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.Conflict, new StringContent("track is on air"))).DeleteTrackAsync(id);
        Assert.False(failed.Deleted);
        Assert.Equal("track is on air", failed.Error);
    }

    [TestMethod]
    public async Task InvokeVerb_SingleLinesTheErrorBody_AndDegradesWhenUnreachable()
    {
        var handler = FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.BadRequest, new StringContent("first line\n  second   line\n"));
        var client = CreateClient<ChatApiClient>(handler);

        var request = new InvokeVerbRequest("PlayTrack", "Host", null, new Dictionary<string, string>());
        var (result, error) = await client.InvokeVerbAsync(request);
        Assert.Null(result);
        Assert.Equal("first line second line", error);

        var dead = CreateClient<ChatApiClient>(new FakeHttpMessageHandler(_ => throw new HttpRequestException("down")));
        var (_, deadError) = await dead.InvokeVerbAsync(request);
        Assert.Equal("Orchestrator not reachable.", deadError);
    }

    [TestMethod]
    public async Task ApproveApproval_ReturnsOkTuple_AndDegradesWhenUnreachable()
    {
        var ok = await CreateClient<ChatApiClient>(FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, new StringContent(""))).ApproveApprovalAsync(Guid.NewGuid());
        Assert.True(ok.Ok);
        Assert.Null(ok.Error);

        var dead = await CreateClient<ChatApiClient>(new FakeHttpMessageHandler(_ => throw new HttpRequestException("down")))
            .ApproveApprovalAsync(Guid.NewGuid());
        Assert.False(dead.Ok);
        Assert.Equal("Orchestrator not reachable.", dead.Error);
    }

    [TestMethod]
    public async Task SubmitGreeting_MapsStatusCodesToListenerFriendlyMessages()
    {
        var request = new SubmitGreetingDto("Anna", "Hi from Berlin", "greeting");

        var (ok, okMessage) = await CreateClient<StationApiClient>(FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, new StringContent(""))).SubmitGreetingAsync(request);
        Assert.True(ok);
        Assert.Equal("Your message is in the queue!", okMessage);

        var (limited, limitedMessage) = await CreateClient<StationApiClient>(FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.TooManyRequests, new StringContent(""))).SubmitGreetingAsync(request);
        Assert.False(limited);
        Assert.Equal("Easy there — try again a bit later.", limitedMessage);

        var (disabled, disabledMessage) = await CreateClient<StationApiClient>(FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.Forbidden, new StringContent(""))).SubmitGreetingAsync(request);
        Assert.False(disabled);
        Assert.Equal("Greetings are currently disabled.", disabledMessage);
    }

    [TestMethod]
    public async Task TestStudio_NeverThrows_AndReportsUnreachableAsAFailedTest()
    {
        var dead = CreateClient<StudiosApiClient>(new FakeHttpMessageHandler(_ => throw new HttpRequestException("down")));

        var result = await dead.TestStudioAsync(new TestStudioDto("VoiceBooth", "local", "http://x", null, null));

        Assert.False(result!.Ok);
        Assert.Equal("Orchestrator not reachable.", result.Detail);
    }

    [TestMethod]
    public async Task SaveNewsProductionSettings_ReturnsNullOnSuccess_AndTheBodyOnFailure()
    {
        var settings = new SaveNewsProductionSettingsDto(
            NewsEnabled: true, NewsExtractionEnabled: false, NewsPackageCadenceMinutes: 60,
            NewsPackageMaxDurationSeconds: 300, NewsPresenterModeratorId: null,
            TopOfHourFadeOutSeconds: 4, TopOfHourIntroGraceSeconds: 15, NewsCategoryOrder: []);

        var okClient = CreateClient<ProductionApiClient>(FakeHttpMessageHandler.RespondingWith(HttpStatusCode.OK, new StringContent("")));
        Assert.Null(await okClient.SaveNewsProductionSettingsAsync(settings));

        var failClient = CreateClient<ProductionApiClient>(FakeHttpMessageHandler.RespondingWith(
            HttpStatusCode.BadRequest, new StringContent("no news host")));
        Assert.Equal("no news host", await failClient.SaveNewsProductionSettingsAsync(settings));
    }

    [TestMethod]
    public void MediaUrls_AreSameOriginProxyPaths()
    {
        var id = Guid.NewGuid();

        Assert.Equal($"/media/track/{id}", MediaUrls.Track(id));
        Assert.Equal($"/media/announcement/{id}", MediaUrls.Announcement(id));
        Assert.Equal($"/media/jingle/{id}", MediaUrls.Jingle(id));
        Assert.Equal("/media/voice-preview/qv-nova", MediaUrls.VoicePreview("qv-nova"));
    }

    // --- harness -----------------------------------------------------------------

    private static T CreateClient<T>(
        FakeHttpMessageHandler handler, FakeHttpMessageHandler? longHandler = null)
        where T : ApiClientBase
        => (T)Activator.CreateInstance(
            typeof(T),
            handler.CreateClient(),
            new SingleClientFactory(() => (longHandler ?? handler).CreateClient()),
            Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(typeof(T)))!)!;

    private sealed class SingleClientFactory(Func<HttpClient> createClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => createClient();
    }
}

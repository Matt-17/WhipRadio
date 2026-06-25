using WhipRadio.Core.Abstractions;
using WhipRadio.Core.Entities;
using WhipRadio.Infrastructure.Llm;

namespace WhipRadio.Infrastructure.Tests;

[TestClass]
public class MusicCopywriterTests
{
    private sealed class CapturingLlm(string reply) : ITextGenerationService
    {
        public string? SystemPrompt { get; private set; }

        public string? UserPrompt { get; private set; }

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            SystemPrompt = systemPrompt;
            UserPrompt = userPrompt;
            return Task.FromResult(reply);
        }
    }

    [TestMethod]
    public async Task DesignArtistAsync_ParsesRichArtistProfileAndMembers()
    {
        var llm = new CapturingLlm("""
{
  "name": "Pacific Furnace",
  "type": "Band",
  "genre": "metal",
  "subgenre": "volcanic doom metal",
  "origin": "Hilo, Hawaii",
  "formationYear": 2018,
  "style": "Down-tuned guitars, ritual floor toms, slack-key melodic fragments, ocean-field recordings, and three-part shouted choruses.",
  "language": "en",
  "shortBiography": "Pacific Furnace are a five-piece heavy band from Hilo, Hawaii, turning volcanic pressure and harbor work into slow, heavy songs.",
  "deepBackgroundBiography": "The band formed after night shifts unloading freight near Hilo Bay. Their songs circle lava fields, family pressure, storm warnings, and the patience of island labor. The rhythm section favors long crescendos because they rehearse by counting surf sets outside the warehouse.",
  "promotionText": "Island heat, steel strings, and slow-burning riffs for the station's heaviest shelf.",
  "members": [
    {
      "name": "Makoa Hale",
      "role": "lead vocals",
      "biography": "Makoa writes most lyrics after long drives across Saddle Road and treats every chorus like a flare.",
      "voiceCreationPrompt": "Baritone voice, rough edge, Hawaiian English accent, close dynamic mic, intense but controlled."
    },
    {
      "name": "Tessa Burdinsky",
      "role": "bass",
      "biography": "Tessa locks the band to slow, stubborn bass figures and keeps a notebook of local ghost stories.",
      "voiceCreationPrompt": "Low calm speaking voice, dry humor, subtle rasp, steady microphone presence."
    }
  ]
}
""");
        var writer = new MusicCopywriter(llm);

        var plan = await writer.DesignArtistAsync(
            "heavy island band with five members from Hilo",
            genre: null,
            subgenre: null,
            existingNames: ["Old Signal"],
            CancellationToken.None);

        Assert.Equal("Pacific Furnace", plan.Name);
        Assert.Equal("Band", plan.Type);
        Assert.Equal("metal", plan.Genre);
        Assert.Equal("volcanic doom metal", plan.Subgenre);
        Assert.Equal("Hilo, Hawaii", plan.Origin);
        Assert.Equal(2018, plan.FormationYear);
        Assert.Equal("en", plan.Language);
        Assert.Contains("volcanic pressure", plan.ShortBiography);
        Assert.Contains("night shifts", plan.DeepBackgroundBiography);
        Assert.Contains("Island heat", plan.PromotionText);
        Assert.Equal(2, plan.Members.Count);
        Assert.Equal("Makoa Hale", plan.Members[0].Name);
        Assert.Equal("lead vocals", plan.Members[0].Role);
        Assert.Contains("Baritone voice", plan.Members[0].VoiceCreationPrompt);

        Assert.Contains("heavy island band with five members from Hilo", llm.UserPrompt);
        Assert.Contains("Existing artist names to avoid", llm.UserPrompt);
        Assert.Contains("Old Signal", llm.UserPrompt);
        Assert.Contains("Return valid JSON only", llm.UserPrompt);
        Assert.Contains("\"members\"", llm.UserPrompt);
    }

    [TestMethod]
    public async Task DesignArtistAsync_RejectsMalformedFunctionStyleProfile()
    {
        var llm = new CapturingLlm("""
Name(Jotun Surf)
Type(Band)
Genre(Electronic Dance Music)
Subgenre(Tribal Techno)
Member(Bjorn Iron-Fist, Vocals/Percussion", A malformed member row.",Deep background: not a voice prompt.)
""");
        var writer = new MusicCopywriter(llm);

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.DesignArtistAsync(
            "techno viking band with tropical beats and 3 members",
            genre: null,
            subgenre: null,
            existingNames: [],
            CancellationToken.None));
    }

    [TestMethod]
    public async Task PlanSongAsync_ParsesArtistSongPlanAndPromptsWithHistory()
    {
        var llm = new CapturingLlm(""""
{
  "title": "Manana al Anden",
  "style": "Motorik drums, glassy analog synth hooks, clipped bass, and a patient sunrise build with warm Spanish lead vocals.",
  "language": "es",
  "vocals": true,
  "durationSeconds": 205,
  "story": "Las Curvas wrote it after a delayed train turned into a sunrise rehearsal, answering the restlessness of Luces Viejas.",
  "lyrics": "Esperamos en el anden\nLa manana sube\nLa ciudad respira\nVolvemos al sur"
}
"""");
        var writer = new MusicCopywriter(llm);
        var artist = new Artist
        {
            Name = "Las Curvas",
            Genre = "rock",
            Subgenre = "motorik art rock",
            StyleDescriptor = "Spanish motorik rock with analog synth color.",
            Type = "Band",
            Origin = "Madrid, Spain",
            Language = "es",
            Biography = "A Spanish band formed after night shifts around Madrid Chamartin.",
            Members =
            {
                new ArtistMember
                {
                    SortOrder = 0,
                    Name = "Mara Luz",
                    Role = "lead vocals",
                    Biography = "Mara writes compact commuter lyrics.",
                    VoiceCreationPrompt = "Female alto, Spanish accent, focused and close-mic.",
                },
            },
        };
        var history = new[]
        {
            new ArtistSongHistoryItem(
                "Luces Viejas",
                "Dry motorik drums and narrow synth lines.",
                "es",
                HasVocals: true,
                SongStory: "Their first song about late-shift commuters.",
                TargetDurationSeconds: 190,
                DurationSeconds: 188,
                UpVotes: 7,
                DownVotes: 2),
        };

        var plan = await writer.PlanSongAsync(
            artist,
            history,
            ["Luces Viejas"],
            "en",
            minDurationSeconds: 150,
            maxDurationSeconds: 240,
            supportsVocals: true,
            CancellationToken.None);

        Assert.Equal("Manana al Anden", plan.Title);
        Assert.Equal("es", plan.Language);
        Assert.True(plan.WantVocals);
        Assert.Equal(205, plan.TargetDurationSeconds);
        Assert.Contains("Motorik drums", plan.Style);
        Assert.Contains("delayed train", plan.Story);
        Assert.Contains("Esperamos en el anden", plan.Lyrics);

        Assert.Contains("A Spanish band formed", llm.UserPrompt);
        Assert.Contains("Canonical song language: es", llm.UserPrompt);
        Assert.Contains("Mara Luz", llm.UserPrompt);
        Assert.Contains("Female alto", llm.UserPrompt);
        Assert.Contains("Luces Viejas", llm.UserPrompt);
        Assert.Contains("likes 7, dislikes 2", llm.UserPrompt);
        Assert.Contains("Never infer a non-default language from Nordic", llm.UserPrompt);
    }

    [TestMethod]
    public async Task PlanSongAsync_FallsBackToEnglishWhenNonDefaultLanguageIsNotExplicitlySupported()
    {
        var llm = new CapturingLlm(""""
{
  "title": "El Aliento del Hielo",
  "style": "A slow-moving soundscape built from heavily processed guitar washes, synthetic cello drones, and sparse melancholic vocals.",
  "language": "es",
  "vocals": true,
  "durationSeconds": 285,
  "story": "The band explored Svalbard field recordings and human fragility against deep time.",
  "lyrics": "El hielo guarda la voz\nNada se mueve aqui\nLa noche pesa mas\nEl viento vuelve al mar"
}
"""");
        var writer = new MusicCopywriter(llm);
        var artist = new Artist
        {
            Name = "The Glacial Almanac",
            Type = "Trio",
            Genre = "Post-Rock",
            Subgenre = "Ambient Shoegaze",
            Origin = "Svalbard, Norwegian Sea",
            Language = "en",
            StyleDescriptor = "Glacial guitar washes, deep bass drones, sparse melancholic layered vocals.",
            DeepBackgroundBiography =
                "The band formed at a research outpost in Svalbard. Their language identity is English with Nordic imagery and sparse phrasing.",
            Members =
            {
                new ArtistMember
                {
                    SortOrder = 0,
                    Name = "Solveig Ljungqvist",
                    Role = "Vocals, Electric Guitar",
                    Biography = "Solveig is the primary vocalist and lyrical center.",
                    VoiceCreationPrompt = "Whispery, breathy soprano with a distinct Scandinavian accent.",
                },
            },
        };

        var plan = await writer.PlanSongAsync(
            artist,
            [],
            [],
            "en",
            minDurationSeconds: 150,
            maxDurationSeconds: 360,
            supportsVocals: true,
            CancellationToken.None);

        Assert.Equal("en", plan.Language);
        Assert.False(plan.WantVocals);
        Assert.Null(plan.Lyrics);
        Assert.Contains("Solveig Ljungqvist", llm.UserPrompt);
        Assert.Contains("Whispery, breathy soprano", llm.UserPrompt);
        Assert.Contains("Canonical song language: en", llm.UserPrompt);
        Assert.Contains("ACE-Step caption", llm.UserPrompt);
        Assert.Contains("ACE-Step temporal script", llm.UserPrompt);
        Assert.Contains("[Verse 1]", llm.UserPrompt);
        Assert.Contains("6-10 syllables per line", llm.UserPrompt);
        Assert.Contains("memorable chorus hook", llm.UserPrompt);
    }

    [TestMethod]
    public async Task PlanSongAsync_AcceptsUnquotedSingleValueFieldsAndOptionalInstrumentalLyrics()
    {
        var llm = new CapturingLlm("""
{
  "title": "Cobalt Ink & Coastal Haze",
  "style": "The track is built around a muted, slightly detuned Rhodes piano loop over slow, gentle sub-bass 808s; it features intermittent bursts of magnified vinyl hiss and distant harbor ambience at 85 BPM.",
  "language": "en",
  "vocals": false,
  "durationSeconds": 320,
  "story": "This piece was inspired after spending an afternoon cataloging old travel ephemera. It builds upon the quiet introspection of Velvet Transit Postcard Signals."
}
""");
        var writer = new MusicCopywriter(llm);
        var artist = new Artist
        {
            Name = "Tidal Static Parade",
            Genre = "indie rock",
            Subgenre = "surf rock",
            StyleDescriptor = "surf rock with tape-warbled production",
            Biography = "A coastal project obsessed with travel ephemera.",
        };

        var plan = await writer.PlanSongAsync(
            artist,
            [],
            [],
            "en",
            minDurationSeconds: 150,
            maxDurationSeconds: 480,
            supportsVocals: true,
            CancellationToken.None);

        Assert.Equal("Cobalt Ink & Coastal Haze", plan.Title);
        Assert.Contains("Rhodes piano", plan.Style);
        Assert.Equal("en", plan.Language);
        Assert.False(plan.WantVocals);
        Assert.Null(plan.Lyrics);
        Assert.Equal(320, plan.TargetDurationSeconds);
        Assert.Contains("travel ephemera", plan.Story);
    }

    [TestMethod]
    public async Task PlanSongAsync_AllowsInstrumentalPlanWithoutLyricsField()
    {
        var llm = new CapturingLlm("""
{
  "title": "Harbor Tape Loop",
  "style": "Muted Rhodes piano, slow sub-bass, tape saturation, and harbor field recordings.",
  "language": "en",
  "vocals": false,
  "durationSeconds": 260,
  "story": "The artist built the piece from catalog notes and old travel postcards."
}
""");
        var writer = new MusicCopywriter(llm);
        var artist = new Artist
        {
            Name = "Tidal Static Parade",
            Genre = "indie rock",
            Subgenre = "surf rock",
            StyleDescriptor = "surf rock with tape-warbled production",
            Biography = "A coastal project obsessed with travel ephemera.",
        };

        var plan = await writer.PlanSongAsync(
            artist,
            [],
            [],
            "en",
            minDurationSeconds: 150,
            maxDurationSeconds: 480,
            supportsVocals: true,
            CancellationToken.None);

        Assert.Equal("Harbor Tape Loop", plan.Title);
        Assert.False(plan.WantVocals);
        Assert.Null(plan.Lyrics);
    }

    [TestMethod]
    public async Task PlanSongAsync_ForcesInstrumentalWhenVocalsAreUnsupported()
    {
        var llm = new CapturingLlm(""""
{
  "title": "Cloud Machines",
  "style": "Slow ambient pads, brushed percussion, and a wide instrumental outro.",
  "language": "en",
  "vocals": true,
  "durationSeconds": 400,
  "story": "The artist made it as a quiet response to their previous single.",
  "lyrics": "These words should not be used."
}
"""");
        var writer = new MusicCopywriter(llm);
        var artist = new Artist
        {
            Name = "Cloud Archive",
            Genre = "electronic",
            Subgenre = "ambient",
            StyleDescriptor = "Slow ambient electronics.",
            Biography = "A studio project centered on patient textures.",
        };

        var plan = await writer.PlanSongAsync(
            artist,
            [],
            [],
            "en",
            minDurationSeconds: 120,
            maxDurationSeconds: 180,
            supportsVocals: false,
            CancellationToken.None);

        Assert.False(plan.WantVocals);
        Assert.Null(plan.Lyrics);
        Assert.Equal(180, plan.TargetDurationSeconds);
        Assert.Contains("Vocals are not available", llm.UserPrompt);
    }

    [TestMethod]
    public async Task PlanSongAsync_ForcesInstrumentalWhenArtistHasNoVocalMember()
    {
        var llm = new CapturingLlm(""""
{
  "title": "Cable Weather",
  "style": "Taut bass pulses, rusted percussion, and a shouted lead vocal hook.",
  "language": "en",
  "vocals": true,
  "durationSeconds": 180,
  "story": "The band built it from utility room hum and late dock work.",
  "lyrics": "These words should not be used."
}
"""");
        var writer = new MusicCopywriter(llm);
        var artist = new Artist
        {
            Name = "Breaker Map",
            Genre = "electronic",
            Subgenre = "industrial ambient",
            StyleDescriptor = "Instrumental industrial ambient with no vocalist.",
            Biography = "A band built around machines and tape loops.",
            Members =
            {
                new ArtistMember
                {
                    SortOrder = 0,
                    Name = "Ira Coil",
                    Role = "modular synths",
                    Biography = "Ira patches the machines.",
                    VoiceCreationPrompt = "Low dry speaking voice.",
                },
                new ArtistMember
                {
                    SortOrder = 1,
                    Name = "Tem Kline",
                    Role = "drums",
                    Biography = "Tem builds the percussion rig.",
                    VoiceCreationPrompt = "Quick clipped speaking voice.",
                },
            },
        };

        var plan = await writer.PlanSongAsync(
            artist,
            [],
            [],
            "en",
            minDurationSeconds: 120,
            maxDurationSeconds: 220,
            supportsVocals: true,
            CancellationToken.None);

        Assert.False(plan.WantVocals);
        Assert.Null(plan.Lyrics);
        Assert.Contains("no member assigned to a vocal role", llm.UserPrompt);
        Assert.Contains("no member is assigned to lead vocals", llm.UserPrompt);
        Assert.DoesNotContain("Ira Coil (modular synths):", llm.UserPrompt);
    }

    [TestMethod]
    public async Task PlanArtistPostAsync_ParsesPostAndIncludesContext()
    {
        var llm = new CapturingLlm("""{"shouldPost":true,"text":"We tuned the harbor until it answered back."}""");
        var writer = new MusicCopywriter(llm);
        var artist = new Artist
        {
            Name = "Harbor Signal",
            Type = "Band",
            Genre = "electronic",
            Subgenre = "dock synth",
            Origin = "Rotterdam",
            FormationYear = 2024,
            Language = "en",
            StyleDescriptor = "Crane field recordings and analog bass.",
            CreationHint = "harbor band",
            Biography = "A band formed near the freight cranes.",
            DeepBackgroundBiography = "They rehearse after midnight by the docks.",
            PromotionText = "Port lights on tape.",
            Members =
            {
                new ArtistMember
                {
                    SortOrder = 0,
                    Name = "Mara Voss",
                    Role = "lead vocals",
                    Biography = "Writes from dockside notes.",
                    VoiceCreationPrompt = "Close alto voice.",
                },
            },
        };
        var track = new Track
        {
            Title = "Signal Lamp",
            Style = "Glass synths and tight drum machines.",
            Language = "en",
            HasVocals = true,
            Lyrics = "light on the water",
            SongStory = "Written after a fogbound load-in.",
            TargetDurationSeconds = 180,
            DurationSeconds = 178,
            Backend = "ace-step",
            GenerationPrompt = "stored generation prompt",
        };

        var plan = await writer.PlanArtistPostAsync(
            artist,
            [new ArtistRecentPostItem("ArtistCreated", "First night on the dock.", DateTime.UtcNow, null)],
            ArtistPostKind.TrackReleased,
            track,
            [new ArtistSongHistoryItem("Old Lamp", "Older style", "en", true, "Older story", 170, 169, 3, 1)],
            CancellationToken.None);

        Assert.True(plan.ShouldPost);
        Assert.Equal("We tuned the harbor until it answered back.", plan.Text);
        Assert.Contains("song-publishing post", llm.UserPrompt);
        Assert.Contains("artist feed", llm.UserPrompt);
        Assert.Contains("will not show the song title anywhere else", llm.UserPrompt);
        Assert.Contains("include it naturally inside the message", llm.UserPrompt);
        Assert.Contains("public place to advertise it", llm.UserPrompt);
        Assert.Contains("stored song story/background", llm.UserPrompt);
        Assert.Contains("one paragraph and no more than two sentences", llm.UserPrompt);
        Assert.Contains("Mara Voss", llm.UserPrompt);
        Assert.Contains("First night on the dock", llm.UserPrompt);
        Assert.Contains("Signal Lamp", llm.UserPrompt);
        Assert.Contains("Written after a fogbound load-in", llm.UserPrompt);
        Assert.Contains("stored generation prompt", llm.UserPrompt);
        Assert.Contains("Old Lamp", llm.UserPrompt);
    }

    [TestMethod]
    public async Task PlanArtistPostAsync_ParsesSkip()
    {
        var llm = new CapturingLlm("""{"shouldPost":false,"text":"The act stays silent after releases."}""");
        var writer = new MusicCopywriter(llm);

        var plan = await writer.PlanArtistPostAsync(
            new Artist { Name = "Quiet Relay", Genre = "ambient", Subgenre = "ambient", StyleDescriptor = "minimal" },
            [],
            ArtistPostKind.ArtistCreated,
            track: null,
            songHistory: [],
            CancellationToken.None);

        Assert.False(plan.ShouldPost);
        Assert.Contains("stays silent", plan.Text);
        Assert.Contains("introduction post", llm.UserPrompt);
        Assert.DoesNotContain("Newly released song context", llm.UserPrompt);
    }
}

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
        var llm = new CapturingLlm(""""""
Title("Morgens am Gleis")
Style("Motorik drums, glassy analog synth hooks, clipped bass, and a patient sunrise build with warm German lead vocals.")
Language("de")
Vocals("yes")
DurationSeconds(205)
Story("Die Kurvenlichter wrote it after a delayed train turned into a sunrise rehearsal, answering the restlessness of Alte Funken.")
Lyrics("""
Wir warten am Gleis
Der Morgen wird heiss
Die Stadt atmet ein
Wir fahren heim
""")
"""""");
        var writer = new MusicCopywriter(llm);
        var artist = new Artist
        {
            Name = "Die Kurvenlichter",
            Genre = "rock",
            Subgenre = "krautrock",
            StyleDescriptor = "German motorik rock with analog synth color.",
            Type = "Band",
            Origin = "Essen, Germany",
            Language = "de",
            Biography = "A German band formed after night shifts around Essen Hauptbahnhof.",
            Members =
            {
                new ArtistMember
                {
                    SortOrder = 0,
                    Name = "Mara Licht",
                    Role = "lead vocals",
                    Biography = "Mara writes compact commuter lyrics.",
                    VoiceCreationPrompt = "Female alto, German accent, focused and close-mic.",
                },
            },
        };
        var history = new[]
        {
            new ArtistSongHistoryItem(
                "Alte Funken",
                "Dry motorik drums and narrow synth lines.",
                "de",
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
            ["Alte Funken"],
            "en",
            minDurationSeconds: 150,
            maxDurationSeconds: 240,
            supportsVocals: true,
            CancellationToken.None);

        Assert.Equal("Morgens am Gleis", plan.Title);
        Assert.Equal("de", plan.Language);
        Assert.True(plan.WantVocals);
        Assert.Equal(205, plan.TargetDurationSeconds);
        Assert.Contains("Motorik drums", plan.Style);
        Assert.Contains("delayed train", plan.Story);
        Assert.Contains("Wir warten am Gleis", plan.Lyrics);

        Assert.Contains("A German band formed", llm.UserPrompt);
        Assert.Contains("Canonical song language: de", llm.UserPrompt);
        Assert.Contains("Mara Licht", llm.UserPrompt);
        Assert.Contains("Female alto", llm.UserPrompt);
        Assert.Contains("Alte Funken", llm.UserPrompt);
        Assert.Contains("likes 7, dislikes 2", llm.UserPrompt);
        Assert.Contains("Never infer German from Nordic", llm.UserPrompt);
    }

    [TestMethod]
    public async Task PlanSongAsync_FallsBackToEnglishWhenGermanIsNotExplicitlySupported()
    {
        var llm = new CapturingLlm(""""""
Title("Der Atem des Eisgebiets")
Style("A slow-moving soundscape built from heavily processed guitar washes, synthetic cello drones, and sparse melancholic vocals.")
Language("de")
Vocals("yes")
DurationSeconds(285)
Story("The band explored Svalbard field recordings and human fragility against deep time.")
Lyrics("""
Was das Eis birgt unter sich
Kein Geräusch nur Gewicht
Die Zeit zählt nicht mehr
Der Wind singt die Kälte
""")
"""""");
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
    }

    [TestMethod]
    public async Task PlanSongAsync_AcceptsUnquotedSingleValueFieldsAndOptionalInstrumentalLyrics()
    {
        var llm = new CapturingLlm("""
Title(Cobalt Ink & Coastal Haze)
Style(The track is built around a muted, slightly detuned Rhodes piano loop over slow, gentle sub-bass 808s; it features intermittent bursts of magnified vinyl hiss and distant harbor ambience at 85 BPM.)
Language(en)
Vocals(no)
DurationSeconds(320)
Story(This piece was inspired after spending an afternoon cataloging old travel ephemera. It builds upon the quiet introspection of Velvet Transit Postcard Signals.)
Lyrics(
)
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
Title("Harbor Tape Loop")
Style("Muted Rhodes piano, slow sub-bass, tape saturation, and harbor field recordings.")
Language("en")
Vocals("no")
DurationSeconds(260)
Story("The artist built the piece from catalog notes and old travel postcards.")
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
        var llm = new CapturingLlm(""""""
Title("Cloud Machines")
Style("Slow ambient pads, brushed percussion, and a wide instrumental outro.")
Language("en")
Vocals("yes")
DurationSeconds(400)
Story("The artist made it as a quiet response to their previous single.")
Lyrics("""
These words should not be used.
""")
"""""");
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
}

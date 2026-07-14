# WhipRadio — Phase 6a Brief: Safe Metadata Enrichment

> Design brief. This feature enriches imported WAV/MP3 files with legally low-friction,
> keyless metadata. The priority is not perfect recognition. The priority is a simple
> user experience, reproducible local data, provenance, and sources that remain suitable
> for an open-source project with possible commercial use.
>
> Fits before or alongside Phase 6's external-world work. Phase 6 adds real-world
> knowledge for imported music; this feature first gives imported tracks stable identity,
> clean metadata, and radio-relevant audio analysis.
>
> **Status 2026-07-05: implemented as the "Archive" feature**, with accepted deviations
> from this brief (see `Phase-0-Tech-Decisions.md` §"Imported Music, Metadata & Knowledge"):
>
> - **Live MusicBrainz web API instead of a local CC0 index.** The keyless web service
>   (1 req/s via `MusicBrainzRateGate`, descriptive User-Agent, no HTTP auto-retries)
>   anchors identity; results are cached in the DB, so normal operation stays
>   offline-friendly. Milestones M3 (local index) and M7 (package update flow) are
>   superseded; the local-index approach remains the escape hatch if rate limits pinch
>   on very large imports.
> - **`TrackSource` enum instead of `Backend="library"` alone** as the discriminator
>   (`Generated`/`Uploaded`/`External`); `Backend` stays a generation-provider id.
> - Sources: read-only external folders from appsettings (`Library:ExternalMusicFolders`)
>   plus drag-drop uploads on the Archive page (stored under `data/archive/uploads/`,
>   deletable — external files never are).

---

## 1. Goal

When a user imports a folder of WAV/MP3 files, WhipRadio should automatically enrich the
library using only supported, open, locally cacheable sources and its own analysis.

The user-facing promise:

```text
WhipRadio can improve imported audio metadata using open local data and audio analysis.
No API keys are required.
```

The result is not guaranteed to be perfect. Ambiguous matches are stored as candidates and
surfaced for review instead of being guessed into the library.

---

## 2. Product decision

### 2.1 Supported sources only

WhipRadio supports these metadata inputs:

| Source | Purpose | Key required | Storage model |
|---|---|---:|---|
| File tags | Existing title, artist, album, year, track number, ISRC, MusicBrainz IDs | No | Stored as user/file-provided claims |
| File name and folder heuristics | Fallback clues when file tags are weak | No | Stored as low-confidence import clues |
| Local MusicBrainz CC0 index | Artist, recording, release, release group, label, ISRC, canonical IDs | No | Downloaded/indexed locally |
| Local MusicBrainz canonical data | Collapse duplicates/versions onto canonical recording groups where available | No | Optional local index |
| Wikidata structured facts | Artist origin, formation, members, structured identifiers, simple factual claims | No | Cached structured facts only |
| WhipRadio audio analysis | Duration, loudness, BPM, energy, intro/outro, cue points | No | WhipRadio-owned analysis data |

### 2.2 No negative provider list

Unsupported providers should not appear in the normal product surface.

Do not add:

- UI toggles for unavailable providers.
- settings sections for future external providers.
- empty provider classes or stubs.
- onboarding text explaining sources WhipRadio does not use.

The UI and documentation should describe only the supported positive model: local file
metadata, local open indexes, structured public-domain facts, and WhipRadio's own audio
analysis.

### 2.3 No required accounts

The default import path must not require:

- API keys.
- provider registration.
- user-specific developer accounts.
- paid external metadata subscriptions.

Optional future extension points may exist at the architecture boundary, but they are not
part of this feature and should not be visible in the first implementation.

---

## 3. Source policy

### 3.1 MusicBrainz

Use MusicBrainz as the central music identity backbone.

WhipRadio should prefer local database/index files instead of frequent live API calls.
Relevant official source notes:

- MusicBrainz core database data is CC0/public-domain style data.
- The public database dumps include CC0 core dumps.
- Canonical MusicBrainz data can help map different recordings/releases to canonical
  entities; only datasets with clearly compatible licensing should be imported.

Implementation rule:

> MusicBrainz IDs are identifiers and matching anchors. WhipRadio stores the IDs,
> selected normalized facts, match score, and source provenance. It does not treat a
> MusicBrainz match as automatically verified unless the match confidence is high.

Useful entity types:

| MusicBrainz entity | WhipRadio use |
|---|---|
| Artist | canonical artist identity, sort name, country, type |
| Recording | track-level identity |
| Release | album/single identity, date, country, track list |
| Release Group | album/single grouping |
| Label | optional factual metadata |
| ISRC | strong matching signal |
| URL relations | bridge to structured external IDs, especially Wikidata |

### 3.2 Wikidata structured facts

Use Wikidata only for structured data, not article/page text.

Allowed examples:

- artist origin country/city when present;
- formation date;
- dissolution date;
- genre claims when present;
- member relationships for bands;
- external IDs;
- simple factual claims useful for host intros.

Do not store unstructured article text as part of this feature.

WhipRadio should turn structured facts into a compact internal `KnowledgeDigest` for
prompt context. The digest is generated from facts, not copied prose.

### 3.3 Own audio analysis

Radio-relevant audio data should be generated by WhipRadio itself:

- duration;
- sample rate/channels/codec;
- loudness/LUFS;
- peak and true-peak if available;
- BPM/tempo estimate;
- energy curve;
- intro end;
- outro start;
- cue points;
- silence or long fade detection.

This belongs to the existing analysis sidecar/domain path, not to external metadata.

---

## 4. Import pipeline

### 4.1 High-level flow

```text
Audio folder selected
  -> discover WAV/MP3 files
  -> read file tags
  -> infer clues from file path
  -> analyse audio technically
  -> build metadata match query
  -> match against local MusicBrainz index
  -> attach structured Wikidata facts if a stable artist ID exists
  -> store metadata claims + provenance
  -> mark track as Matched, Ambiguous, or NeedsReview
```

### 4.2 File discovery

The importer should support:

- recursive folder import;
- WAV and MP3 first;
- duplicate detection by file hash;
- stable re-import without creating duplicate tracks;
- incremental import when new files are added later.

Suggested duplicate keys:

1. exact file hash;
2. existing track ID/path mapping;
3. same audio fingerprint or same technical duration + same file size, only as a weak
   duplicate hint.

No external fingerprint service is required for the default feature.

### 4.3 Tag reading

Read from the file when available:

- title;
- artist / album artist;
- album;
- track number;
- disc number;
- year/date;
- genre;
- ISRC;
- MusicBrainz Artist ID;
- MusicBrainz Recording ID;
- MusicBrainz Release ID;
- MusicBrainz Release Group ID.

Treat file tags as evidence, not as verified truth. The user may have wrong tags.

### 4.4 File-name heuristics

Use conservative heuristics:

```text
Artist - Title.mp3
01 - Title.mp3
Artist/Album/01 Title.mp3
Album/Artist - Title.wav
```

Heuristics should never produce high-confidence matches alone. They are query hints and
UI suggestions.

### 4.5 Normalization

Before matching, normalize strings:

- trim whitespace;
- normalize Unicode;
- case-fold;
- remove duplicate spaces;
- normalize punctuation variants;
- strip common track number prefixes;
- keep original values separately for display.

Do not over-normalize artist names. Aggressive normalization can merge legitimately
different artists.

---

## 5. Matching strategy

### 5.1 Confidence model

A track match produces zero or more `MetadataCandidate` records. Each candidate has a
score and a reason list.

Suggested confidence bands:

| Score | Status | Behavior |
|---:|---|---|
| `>= 0.95` | AutoMatched | apply metadata, keep provenance |
| `0.80 - 0.94` | Matched | apply safe fields, surface review badge |
| `0.55 - 0.79` | Ambiguous | store candidates, do not overwrite main display fields |
| `< 0.55` | NeedsReview | keep local tags only |

### 5.2 Scoring inputs

Strong signals:

- existing MusicBrainz Recording ID;
- existing MusicBrainz Release ID;
- exact ISRC match;
- artist + title + duration close to candidate recording;
- album + track number + release match.

Medium signals:

- artist/title fuzzy match;
- album/title fuzzy match;
- release year close to file tag year;
- folder structure matching release/artist.

Weak signals:

- file name only;
- genre tag;
- approximate year only;
- short title-only match.

### 5.3 Auto-match rules

Auto-accept only if at least one strong anchor exists.

Examples:

| Situation | Auto-accept? |
|---|---:|
| File contains MusicBrainz Recording ID and duration roughly matches | Yes |
| File contains ISRC and candidate title/artist roughly match | Yes |
| Artist + title + album + track number + duration all match one candidate | Yes |
| Artist + title has many candidates across many releases | No |
| Filename-only match | No |

### 5.4 Ambiguity handling

For ambiguous matches:

- store all plausible candidates;
- keep original file-tag title/artist visible;
- show a small review state in the library;
- do not interrupt normal radio playout;
- allow bulk review later.

The station can still play ambiguous tracks. It simply should not let hosts make strong
factual claims based on unverified external metadata.

---

## 6. Data model

Exact naming can be adapted to the existing entities, but the feature needs these
concepts.

### 6.1 Track metadata status

Add to `Track` or a related metadata table:

```csharp
public enum MetadataStatus
{
    None = 0,
    LocalOnly = 1,
    AutoMatched = 2,
    Matched = 3,
    Ambiguous = 4,
    NeedsReview = 5,
    Verified = 6,
    Rejected = 7
}
```

Suggested fields:

```csharp
public sealed class Track
{
    public Guid Id { get; set; }
    public string FilePath { get; set; } = "";
    public string? FileHash { get; set; }
    public string? Title { get; set; }
    public string? ArtistName { get; set; }
    public string? AlbumTitle { get; set; }
    public double DurationSeconds { get; set; }
    public MetadataStatus MetadataStatus { get; set; }
    public double? MetadataConfidence { get; set; }
}
```

### 6.2 Metadata claims

Store field-level provenance. This is more important than a single track-level source.

```csharp
public sealed class MetadataClaim
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public MetadataOwnerType OwnerType { get; set; }
    public string FieldName { get; set; } = "";
    public string Value { get; set; } = "";
    public string Source { get; set; } = "";
    public string? SourceEntityId { get; set; }
    public MetadataLicenseClass LicenseClass { get; set; }
    public double Confidence { get; set; }
    public bool IsApplied { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum MetadataLicenseClass
{
    Unknown = 0,
    FileProvided = 1,
    UserProvided = 2,
    CC0 = 3,
    OwnAnalysis = 4
}
```

Default import should only apply claims from `FileProvided`, `UserProvided`, `CC0`, or
`OwnAnalysis`.

### 6.3 External IDs

```csharp
public sealed class ExternalId
{
    public Guid Id { get; set; }
    public MetadataOwnerType OwnerType { get; set; }
    public Guid OwnerId { get; set; }
    public string Source { get; set; } = "";       // MusicBrainz, Wikidata, ISRC
    public string EntityType { get; set; } = "";   // Recording, Artist, Release, Qid
    public string Value { get; set; } = "";
    public double Confidence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
```

### 6.4 Metadata candidates

```csharp
public sealed class MetadataCandidate
{
    public Guid Id { get; set; }
    public Guid TrackId { get; set; }
    public string Source { get; set; } = "MusicBrainz";
    public string SourceEntityId { get; set; } = "";
    public string DisplayTitle { get; set; } = "";
    public string DisplayArtist { get; set; } = "";
    public string? DisplayAlbum { get; set; }
    public double Score { get; set; }
    public string ReasonsJson { get; set; } = "[]";
    public CandidateStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
```

### 6.5 Knowledge digest

```csharp
public sealed class KnowledgeEntry
{
    public Guid Id { get; set; }
    public MetadataOwnerType OwnerType { get; set; }
    public Guid OwnerId { get; set; }
    public string Source { get; set; } = "Wikidata";
    public string SourceEntityId { get; set; } = "";
    public string FactsJson { get; set; } = "{}";
    public string Digest { get; set; } = "";
    public MetadataLicenseClass LicenseClass { get; set; } = MetadataLicenseClass.CC0;
    public DateTimeOffset RetrievedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
```

### 6.6 Audio analysis

If the existing analysis model already contains these fields, reuse it. Otherwise add a
track-level analysis record:

```csharp
public sealed class TrackAudioAnalysis
{
    public Guid TrackId { get; set; }
    public double DurationSeconds { get; set; }
    public double? LoudnessLufs { get; set; }
    public double? PeakDb { get; set; }
    public double? Bpm { get; set; }
    public double? Energy { get; set; }
    public double? IntroEndSeconds { get; set; }
    public double? OutroStartSeconds { get; set; }
    public string CuePointsJson { get; set; } = "[]";
    public DateTimeOffset AnalysedAt { get; set; }
}
```

---

## 7. Architecture placement

WhipRadio's existing rule still applies: Core holds shared business rules, Infrastructure
holds external/persistence adapters, Orchestrator owns long-running station behavior, and
Web stays thin.

### 7.1 Core

Add:

- metadata entities/enums where they are domain-level;
- match scoring models;
- normalization helpers if provider-independent;
- interfaces:
  - `IFileTagReader`;
  - `IMetadataMatcher`;
  - `IMusicMetadataIndex`;
  - `IStructuredFactsProvider`;
  - `IMetadataClaimService`.

### 7.2 Infrastructure

Add implementations:

- file tag reader;
- local MusicBrainz index importer/reader;
- canonical-data importer/reader;
- Wikidata structured facts reader/cache;
- EF persistence for metadata claims, candidates, external IDs, facts.

### 7.3 Orchestrator

Add long-running/background workflows:

- `LibraryImportService` for folder imports;
- `MetadataEnrichmentService` for queued enrichment;
- `MetadataIndexUpdateService` for local data package refresh;
- optional `MetadataReviewService` for accept/reject actions.

The live stream must never block on metadata enrichment. Imported tracks can enter the
library as `LocalOnly` and be enriched later.

### 7.4 Web

Add UI surfaces:

- import progress;
- metadata status badge in the library;
- candidate review drawer/page;
- data package status page.

Keep the wording simple. Do not expose implementation complexity to normal users.

### 7.5 Data root

Suggested storage:

```text
data/
  metadata/
    musicbrainz/
      raw/
      index/
      last-import.json
    wikidata/
      cache/
      last-import.json
  library/
    tracks/
```

Do not commit downloaded metadata packages to the repo.

---

## 8. User experience

### 8.1 Import page copy

```text
Import music

WhipRadio can improve imported track metadata using open local data and audio analysis.
No API keys are required.

[ ] Improve metadata automatically
[ ] Analyse audio for mixing and cue points

[Start import]
```

### 8.2 Data package page copy

```text
Open metadata packages

WhipRadio uses local metadata packages so imports stay fast and do not require API keys.

MusicBrainz core index        Installed / Update available
MusicBrainz canonical index   Not installed
Wikidata artist facts         Installed

[Install missing packages]
[Update packages]
```

### 8.3 Library status badges

| Badge | Meaning |
|---|---|
| `Local only` | File tags and audio analysis only |
| `Matched` | Metadata was matched with good confidence |
| `Review` | Several plausible candidates exist |
| `Verified` | User accepted the match |

Avoid source clutter in the main library. Detailed provenance belongs in an advanced
panel.

### 8.4 Review behavior

A review screen should show:

- current file/tag data;
- candidate title/artist/album/date;
- score and short reason list;
- accept/reject buttons;
- option to keep local-only.

Bulk actions:

- accept all high-confidence matches;
- keep all low-confidence matches local-only;
- filter by status.

---

## 9. Prompt-context integration

Hosts should receive factual metadata only when it is trustworthy enough.

Rules:

| Track status | PromptContext behavior |
|---|---|
| `LocalOnly` | Use title/artist only; no factual background claims |
| `Matched` | Use basic metadata; cautious factual digest |
| `Ambiguous` | Do not use factual digest |
| `Verified` | Use full available digest |

For host copy, facts must still be paraphrased. Prompt context should include compact
facts, not source prose.

Example prompt-context fragment:

```text
Current track facts:
- Title: Example Song
- Artist: Example Band
- Metadata confidence: verified
- Artist facts: formed in Dresden; active since 2018; known for synth-heavy indie pop.
Use these as background facts. Do not quote source text.
```

---

## 10. Tag write-back

Do not write enriched metadata back into user files by default.

Reasons:

- WAV metadata compatibility is inconsistent.
- Users may not expect files to be modified.
- The WhipRadio database is the source of truth.
- Bad matches would become harder to undo.

Optional later admin action:

```text
Write selected verified metadata back to files
```

This must be explicit, reversible where possible, and limited to verified tracks.

---

## 11. Milestone spine

### M1 — File import baseline

- Recursive WAV/MP3 discovery.
- File hash and duplicate detection.
- File-tag extraction.
- Path/name heuristics.
- Store imported tracks as `LocalOnly`.

Done when: a folder import populates the library without external network dependency.

### M2 — Audio analysis integration

- Queue imported tracks for analysis.
- Store duration, loudness, energy, BPM, intro/outro, cue points where available.
- Show analysis status in library/admin.

Done when: imported tracks have the same radio-relevant analysis quality as generated
tracks.

### M3 — Local MusicBrainz index

- Download/install local MusicBrainz core data package.
- Build a compact query index for artist/title/recording/release/ISRC.
- Store index version and import date.
- No public API dependency for normal matching.

Done when: imported file tags can be matched against local MusicBrainz data offline.

### M4 — Matching engine

- Candidate generation.
- Confidence scoring.
- Auto-match only with strong anchors.
- Ambiguous candidate storage.
- Field-level metadata claims and provenance.

Done when: bad or uncertain files are not silently misidentified.

### M5 — Wikidata structured facts

- Resolve Wikidata QIDs from stable artist identity where available.
- Cache structured facts.
- Build compact `KnowledgeEntry` digests.
- Feed verified/certain facts into `PromptContextBuilder`.

Done when: hosts can introduce verified real artists with short factual context.

### M6 — Review UI

- Library status badges.
- Candidate review screen.
- Accept/reject/keep-local actions.
- Bulk accept for high-confidence matches.

Done when: ambiguous imports can be cleaned up without editing the database manually.

### M7 — Package update flow

- Install/update open metadata packages from Admin/Settings.
- Show download/indexing progress.
- Keep station playable while packages update.
- Retry/resume failed downloads where practical.

Done when: a normal user can keep metadata indexes current without command-line work.

---

## 12. Definition of Done

- [x] Importing a folder of WAV/MP3 files requires no API keys.
- [x] Default enrichment uses only supported positive sources: file tags, path heuristics,
      MusicBrainz CC0 data, structured Wikidata facts, and WhipRadio audio analysis.
- [x] No UI or settings page advertises unavailable external providers.
- [x] MusicBrainz matching works *(accepted deviation: live keyless web API with a strict
      rate gate instead of a local index; DB caching keeps normal operation offline)*.
- [x] Metadata claims store field-level source, confidence, and license class.
- [x] Ambiguous matches are not auto-applied *(stored as candidates for the review UI)*.
- [x] Host factual intros only use verified or high-confidence metadata
      *(`PromptContextBuilder` gating table by `MetadataStatus`)*.
- [x] No unstructured article text, lyrics, or scraped prose is stored by this feature
      *(Wikipedia summaries are paraphrase input only; digests are LLM-paraphrased facts)*.
- [x] Audio analysis produces radio-relevant fields for mixing and cue planning
      *(imported/MP3 audio staged to temp WAV by `ImportedAudioStager`)*.
- [x] The live stream never blocks on import, indexing, or enrichment
      *(background `LibraryImportService` + `ArchiveEnrichmentService`)*.
- [x] EF migrations are scaffolded normally; `dotnet build` and `dotnet test` stay green
      *(`GuestVoiceFx`, `ArchiveTracks`, `ArchiveEnrichment`)*.

---

## 13. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Poorly tagged files match badly | Keep them `LocalOnly` or `Ambiguous`; do not guess |
| Many versions of the same song | Use release/recording/canonical data, but require confidence |
| Large local metadata packages | Make packages optional/installable; show disk usage before download |
| Wikidata facts are incomplete | Treat facts as additive, not required |
| Overconfident host copy | Gate facts by metadata status and confidence |
| User expects perfect tagging | UI says "improve metadata", not "identify everything" |

---

## 14. Open questions

- Should MusicBrainz canonical data be installed by default, or offered as an additional
  package after the core index?
- Should the first version use full MusicBrainz dumps or a smaller prebuilt searchable
  index produced by WhipRadio releases?
- Should data package installation happen during onboarding, or only when importing the
  first external library?
- Should verified metadata ever be written back into MP3 tags, or should WhipRadio remain
  database-only permanently?
- How much of the review UI belongs on the main Library page versus a dedicated Metadata
  page?

---

## 15. Reference links

Official references to verify source policy during implementation:

- MusicBrainz Data License: https://musicbrainz.org/doc/About/Data_License
- MusicBrainz Database Download: https://musicbrainz.org/doc/MusicBrainz_Database/Download
- Canonical MusicBrainz Data: https://musicbrainz.org/doc/Canonical_MusicBrainz_data
- Wikidata Data Access: https://www.wikidata.org/wiki/Wikidata:Data_access


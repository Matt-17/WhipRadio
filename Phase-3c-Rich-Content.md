# WhipRadio — Phase 3c Brief: Rich Content

> Design brief. Firm where it counts, open where the project's shape should decide.
> Builds on 3a (mixer) and 3b (PromptContextBuilder, memory, priorities).
>
> **Theme:** the station gets *real content* — news and traffic from the world,
> multi-voice talk/podcast segments, and the ability to hit the top of the hour.

---

## 1. Goal

Three capabilities: (1) news & traffic announcements through the existing
`IAnnouncementDataSource` abstraction; (2) a **ConversationSegment** engine that
produces multi-speaker talks and podcasts (same machine, different length/structure);
(3) **top-of-the-hour timing** so news lands at :00.

**Pulled forward from Phase 5:** rich artist creation is part of the content foundation
now. Artists are created from one-line hints through the writer room, with stored public
showcase copy, hidden deep background, and member rosters. ConversationSegment can later
use those members as speakers without replacing the artist schema.

---

## 2. News & Traffic (the easy, high-value part)

The interfaces already exist from Phase 1. This is implementations + scheduling.

- **News:** RSS sources (tagesschau, BBC, configurable list) → fetch top N headlines →
  ScriptWriter summarises into spoken radio copy → TTS. **Firm copyright rule:**
  summarise and rewrite in the host's own words; never read article text verbatim.
- **Traffic:** start with a DE-friendly source. Options: the Autobahn API (no key,
  Germany motorways) or HERE/TomTom (keyed, broader). Recommend Autobahn first for the
  homelab use case; abstract behind `ITrafficSource` so a keyed provider can replace it.
- **Scheduling:** these are `Scheduled` priority announcements (from 3b). News at :00,
  traffic at :20/:50, etc. — all configurable. A dedicated news host is optional
  (mirror the weather specialist pattern from 3b if desired).

**Open Choice:** how much editorial filtering (skip certain categories, dedupe similar
headlines). Recommend a simple per-source headline cap + a recency window; leave
smarter curation as a later refinement.

---

## 3. ConversationSegment engine (talks = podcasts, one machine)

A talk and a podcast are the same artifact at different scales. Model **one** concept:

`ConversationSegment`:
- `Kind` (Talk | Podcast) — really just presets for the fields below
- `Participants` (ordered list of host/guest ids, 2–5)
- `TargetDurationMinutes`
- `Structure` (Freeform | Chaptered) + optional `Chapters[]`
- `Topic` + `Brief` (what it's about)
- `Status` (Planned/Scripted/Produced/Used)
- produced output: a single mixed WAV + a stored transcript

### Production pipeline (the firm part)
1. **Plan** (LLM, reasoning provider): from topic + participants + duration, produce a
   structure — for a podcast, 3–5 chapters with a one-line intent each; for a talk, just
   a beat list. Word budget per chapter derived from each speaker's rate (reuse 3b's
   word-budget math).
2. **Script** the dialogue. **This is where Phase 5's multi-agent choice looms.** In 3c,
   keep it tractable: a single LLM call can generate a *speaker-tagged* script
   (`[CHARLIE]: …` / `[JENNY]: …`). Phase 5 will upgrade this to true per-agent turns
   (Option B). **Design the `ConversationSegment` so that upgrade needs no schema
   change** — store turns as a list of `(speakerId, text, markers)`, however they were
   generated.
3. **Voice** each turn via that speaker's TTS voice (from their `Moderator`/`Artist`
   record).
4. **Assemble** turns into one WAV. With the 3a mixer, turns can slightly overlap for
   natural interruptions (a third source slot). Keep overlaps small and optional in 3c;
   the 5-people-talking-over-each-other vision is Phase 5.
5. **Schedule:** a segment occupies a format slot (a "talk show" / "podcast" format the
   Program Director can place). Long segments need the mixer's lookahead to pre-produce.

**Open Choice:** produce podcasts fully ahead of time (simpler, safe) vs stream-produced
chapter-by-chapter. Recommend produce-ahead in 3c — a podcast is not time-sensitive and
pre-production avoids any live stall.

---

## 4. Top-of-the-hour timing

The user flagged this as desirable-but-luxury. With the 3a mixer it's now reachable
because the mixer already does sample-accurate scheduling.

**Approach (firm enough):** the ShowRunner gains a `TimingPlanner` that, as :00
approaches, looks at the remaining queue and chooses one of:
- pick a *next track whose duration fits* the remaining time to :00 (selection-time
  solution — cheapest, preferred);
- start the crossfade early / extend an outro to land on :00 (mixer already supports
  early fades);
- drop in a short jingle or station-id to fill a small gap;
- as last resort, a clean hard-cut at :00 into the news (radio does this constantly).

**Firm rule:** never time-stretch music to fit (out of scope; sounds bad). Timing is
solved by *selection and fades*, not tempo manipulation.

**Open Choice:** how tight the target is (±2 s vs exact). Recommend ±2 s for 3c; exact
alignment can come if news/traffic prove popular.

---

## 5. Suggested milestone spine (agent refines)
1. News source(s) + ScriptWriter summarisation + scheduled placement.
2. Traffic source (Autobahn first) behind `ITrafficSource`.
3. `ConversationSegment` model + single-call speaker-tagged scripting + assembly.
4. Talk/podcast formats the Program Director can schedule.
5. `TimingPlanner` for top-of-hour, selection-first with fade/jingle fallbacks.
6. Rich artist creation from hints + member roster storage (pulled forward from Phase 5).

---

## 6. Definition of Done (themes)
- [ ] News announcements: summarised (never verbatim), scheduled, host-voiced
- [ ] Traffic announcements from a DE source, behind a swappable interface
- [ ] A 2-speaker talk and a 3-speaker chaptered podcast both produce a mixed WAV +
      transcript, schedulable as formats
- [ ] `ConversationSegment` stores turns as `(speaker, text, markers)` — ready for
      Phase 5 multi-agent with no schema change
- [ ] News lands within ±2 s of :00 via selection/fades, never via time-stretch
- [ ] Stream stays live throughout; long podcasts pre-produced
- [ ] Artists have enough hidden background and member data to seed future talks

---

## 7. Open questions
- News/traffic sources: which exact feeds, and any region beyond Germany at launch?
- Podcast length ceiling for the homelab (production time vs library churn)?
- Should talk/podcast transcripts surface on the existing /playlog + a new content page?
- How aggressive should top-of-hour be before it's worth the complexity?


infos from sonnet: 

# WhipRadio — Externe Datenquellen ohne API-Key

*Location-Autocomplete · News · Verkehrsmeldungen — Stand: 18. Juni 2026*

---

## 1. Location-Daten (Onboarding / Settings)

Ziel: Der Nutzer tippt einen Ort ein (z. B. „Dresden") und bekommt sortierte Vorschläge nach Wahrscheinlichkeit/Relevanz — inklusive internationaler Gleichnamigkeit (Dresden, Germany vs. Dresden, USA).

### Empfehlung: Photon (Komoot)

Open-Source-Geocoder auf OpenStreetMap-Basis, speziell für Autocomplete entwickelt. Kein API-Key, keine Registrierung, kein Billing.

```
https://photon.komoot.io/api/?q=Dresden&limit=5&lang=de
```

Optional gefiltert auf Städte/Orte:

```
https://photon.komoot.io/api/?q=Dresden&limit=5&osm_tag=place:city&osm_tag=place:town
```

Die Sortierung nach Relevanz/Importance ist bereits in der API eingebaut — größere/bekanntere Orte erscheinen zuerst.

### Warum nicht Nominatim direkt?

Die offizielle Nominatim-API (nominatim.openstreetmap.org) verbietet Autocomplete-Nutzung in ihrer Policy explizit:

> „Auto-complete search – This is not yet supported by Nominatim and you must not implement such a service on the client side using the API."

Zusätzlich gilt ein hartes Rate-Limit von 1 Request/Sekunde. Photon nutzt dieselben OSM-Daten, ist aber genau für diesen Use Case gebaut und erlaubt Live-Suche während des Tippens (mit Debounce, z. B. 300–500 ms empfohlen).

### Entscheidung

| Komponente | Wahl |
|---|---|
| Geocoding/Autocomplete | Photon (photon.komoot.io) |
| Fallback | Keiner — bewusst nicht eingeplant |
| Debounce | ~300–500 ms client-seitig |
| Kosten | Kostenlos, kein Key |

---

## 2. Nachrichten (International / National / Regional)

Ziel: Aktuelle Nachrichten als Grundlage für KI-generierte Radiomoderation — ohne API-Key, ohne feste Kuratierung für „Hunderte Städte", da WhipRadio international funktionieren soll.

### Lösung: RSS + bedarfsgesteuerte Volltext-Extraktion

- RSS-Feed wird periodisch gepollt (z. B. alle 15–30 Minuten) → liefert Titel + Teaser
- LLM (Gemma) bewertet: „Ist das interessant genug für einen Radiobeitrag?"
- Bei „Ja": Artikel-URL wird vollständig abgeholt und der Volltext extrahiert
- LLM generiert daraus den Moderationstext

Dieser zweistufige Ansatz spart Traffic, weil nicht jeder Artikel blind vollständig geladen wird — nur die vom LLM als relevant eingestuften.

### Volltext-Extraktion

Library:

```
npm install @extractus/article-extractor

import { extract } from '@extractus/article-extractor'
const article = await extract(url)
// article.content = sauberer Volltext
```

Basiert auf Mozillas Readability-Algorithmus (derselbe wie in Firefox Reader Mode). Funktioniert ohne API-Key bei den meisten Nachrichtenseiten gut; bei Paywalled-Inhalten wird nur der frei zugängliche Teil extrahiert — für Radio-Zwecke meist ausreichend.

### Feed-Strategie: Kuratierte internationale Defaults + Nutzer-Erweiterung

Da WhipRadio international und ohne festen Kernmarkt funktionieren soll, ist eine statische Mapping-Tabelle pro Stadt/Region nicht praktikabel. Stattdessen:

- Internationale Feeds sind feste, mitgelieferte Defaults (laufen überall)
- Nationale & regionale Feeds trägt der Nutzer selbst ein — mit automatischer Validierung (Test-Fetch prüft, ob es ein gültiger RSS-Feed ist)
- UI-Hinweis beim Onboarding: „Suche nach '[deine Stadt] RSS Feed' oder '[dein Sender] RSS'"
- Langfristig optional: community-gepflegte `feeds.json` im Repo (Open-Source-PRs)

### Beispiel-Defaults (International)

| Quelle | Feed-URL |
|---|---|
| BBC World | feeds.bbci.co.uk/news/world/rss.xml |
| The Guardian (World) | theguardian.com/world/rss |
| NYT World | rss.nytimes.com/services/xml/rss/nyt/World.xml |

### Entscheidung

| Komponente | Wahl |
|---|---|
| Nachrichtenquelle | RSS-Feeds (kein API-Key) |
| International | Feste Defaults (BBC, Guardian, NYT o. ä.) |
| National / Regional | Nutzer trägt eigene Feeds ein, App validiert |
| Volltext bei Bedarf | @extractus/article-extractor (Readability-basiert) |
| Selektionslogik | LLM entscheidet pro Teaser, ob Volltext geholt wird |

---

## 3. Verkehrsmeldungen (Stau, Unfall, Blitzer)

Ziel: Kurze Verkehrshinweise wie „A13 Stau", „A4 Unfall", „Blitzer in Dresden" — keine Echtzeit-Flächendaten, sondern punktuelle Meldungen. International gewünscht.

### Befund: Keine keylose internationale Quelle verfügbar

Anders als bei Geocoding (Photon) und News (RSS) gibt es für Verkehrsmeldungen keine offene, internationale Alternative ohne API-Key. Diese Daten stammen entweder aus behördlichen Systemen (meist nur regional, z. B. nur ein US-Bundesstaat) oder aus kommerziellen Crowd-Netzwerken wie Waze, die lizenziert werden müssen.

### Verfügbare Optionen (alle mit Key)

| Anbieter | Abdeckung | Free-Tier |
|---|---|---|
| TomTom Traffic API | International | 2.500 Requests/Tag kostenlos |
| Waze (via Drittanbieter-API) | International, gut für Unfälle/Blitzer/Stau | Meist kostenpflichtig nach Trial |
| 511.org & ähnliche | Nur regional (z. B. US-Bundesstaat) | Begrenztes Free-Tier, regional |

### Entscheidung

- Traffic-Feature wird als optionales Modul eingeplant, nicht als Pflichtfeature
- TomTom Traffic API mit kostenlosem Key als einzige sinnvolle Option (2.500 Req/Tag reichen für Polling alle 15–30 Min locker)
- Nutzer trägt den TomTom-Key optional im Setup ein — ohne Key bleibt das Verkehrs-Segment in der Sendung einfach deaktiviert

Damit bleibt das Grundprinzip „ohne Key nutzbar" für den Kern von WhipRadio erhalten; Traffic ist die einzige der drei Datenkategorien, die strukturell einen Key erfordert.

---

## Gesamtübersicht

| Datenquelle | Anbieter | API-Key? | Abdeckung |
|---|---|---|---|
| Location-Autocomplete | Photon (Komoot) | Nein | International |
| News | RSS + article-extractor | Nein | International (Defaults) + nutzerdefiniert |
| Verkehrsmeldungen | TomTom Traffic API | Ja (optional, kostenlos) | International |
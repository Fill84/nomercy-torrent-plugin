# Domain

Where the episode list comes from is `docs/02-library.md`. This is what the plugin does with it.

## Episode states

| State | Meaning | Shown as |
| --- | --- | --- |
| `NotAired` | `AirDate` is null or in the future | *waiting to air* — never searched, never counted as missing |
| `Missing` | aired, no file | *looking* |
| `Unavailable` | asked `MaxSearchAttempts` times, nothing acceptable exists | *given up for now* |

`Unavailable` is not permanent. Every maintenance pass re-derives the list from the library; a
release that appears next week puts the episode back to `Missing`. 0.3.4 filtered `Unavailable` out
of the refresh and an episode that went unavailable once was invisible forever.

## Release names

`ReleaseName.Parse` knows two grammars.

**Scene:**

```
Silo.S03E06.1080p.WEB.H264-CAKES
Show.Name.2023.S01E01.2160p.ATVP.WEB-DL.DDP5.1.H.265-GROUP
Show.Name.S01.1080p.WEB-DL.H264-GROUP          ← season pack
```

**Anime:**

```
[SubsPlease] Show Title - 137 (1080p) [A1B2C3D4].mkv
[Erai-raws] Show Title - 13v2 [1080p][Multiple Subtitle]
Show.Title.S02E13.1080p.WEB.x264-GROUP         ← scene-styled anime, also valid
```

| Field | Scene | Anime | Trap |
| --- | --- | --- | --- |
| Title | before the season tag | after `]`, before ` - ` | an anime title can contain a dash; the separator is ` - ` with spaces and the number after it must be digits |
| Season/episode | `S03E06` | absolute after ` - `, **or `EP1173` with no separator at all**, or `S02E13` when present | `137` is an episode, `1080` is not — a bare number is only an episode if not followed by `p`. `E` takes up to four digits: `One Piece S01E1173` is a real row |
| Version | — | `v2`, `v3` | a `v2` supersedes the `v1` of the same episode |
| Quality | `1080p` | `(1080p)`, `[1080p]` | brackets |
| Codec | `H264`, `x264`, `H.264`, `AVC` | same | accept `264`/`265` without a prefix; `H.265` has a dot inside |
| Group | after the last `-` | inside the leading `[...]` | a scene title is full of dashes; the group is after the *last* one and contains no dots |
| Language | `MULTi`, `VOSTFR`, `Dual.Audio`, and the languages the captures name outright — `GERMAN`, `ITA`, `TRUEFRENCH`, `SPANISH`, `RUS`, `POLISH`, `SWESUB`, `JAP`, `ENG` | `[Multiple Subtitle]`, `Dual Audio` | never `Greek`, which is a programme; and never a three-letter abbreviation out of a subtitle list, which is short enough to be something else |
| Pack | `S01` with no `E` | `01~12`, `Batch`, `Complete` | a pack answers for every gap in the season it covers |

`TitleMatcher.Matches`: normalise both sides (lowercase, **accents folded**, punctuation to spaces,
collapse), then the release title must **begin with** the show title and the slot must match.
Beginning with, not containing — *A Bloody Lucky Day* contains *Lucky* and is a different programme.

Counted in **words**, not in letters: *Silos* begins with the letters of *Silo* and is a different
show, and the LimeTorrents capture really does carry a row called `Silos / Silo (2023–)`. Accents
are folded because one Nyaa row writes the same programme both ways in the one title — *Pokémon
Horizons: The Series* and *Pokemon (2023)* — so insisting on the accent refuses a release of exactly
the show that was asked for. A letter is anything a language calls one, so a title written in
Japanese survives being normalised.


### Trackers are learned, not chosen

The owner's decision, 20 August 2026. `DefaultTrackers` starts empty and nobody types into it: every
tracker the plugin comes across is kept — on a magnet, on a listing, on a torrent it is holding —
with no duplicates, and the whole list travels with every grab afterwards. More trackers is a faster
download, and the swarm one release was posted to is usually the swarm the next one is in.

Kept in the order they were first met, so the settings file does not churn, and only what could
actually be announced to: HTTP, HTTPS or UDP, by BEP 3 and BEP 15. A magnet's tracker field carries
whatever was written into it.

**One thing is never kept, and it is not a preference.** A private tracker's announce address carries
the owner's own passkey. This list goes out with every grab, so learning one would hand their
credentials to every public swarm they download from — and print them on the Settings page. Anything
with a query string or with user information before the host is refused, because that is where a
passkey lives and no public tracker needs either. So is anything on a host the owner configured as a
private tracker, whatever the address looks like: their tracker belongs to the torrents it issued and
to nothing else.

## The profile: where each rule applies

| Rule | On the **name** | On the **copy** |
| --- | --- | --- |
| Title matches the show | ✅ | |
| Season and episode match | ✅ | |
| Resolution on the ladder | ✅ | |
| Codec, and codec tag required | ✅ | |
| Language | ✅ | |
| Blocked group | ✅ | *no list of its own — see below* |
| Forbidden terms | ✅ | |
| Season pack allowed | ✅ | |
| Blacklisted title or hash | ✅ | ✅ |
| **Seeders at or above the minimum** | | ✅ |
| **Size within bounds** | | ✅ |

A copy nobody is seeding is refused with a history line naming the site and the count. A site that
does not publish a count has **not** said nought: judging a copy on a number nobody gave is the same
category error as judging a name on one, and it would silently drop every source that leaves the
count out.

**Two rows in that table have no data behind them anywhere in these documents.** *Blocked group* has
no list of groups in the settings, and it is `ExcludeTerms` doing the work: a forbidden term is
looked for in the whole name, and a release group is part of the name it appears in. *Size within
bounds* has no bounds — no setting names a minimum or a maximum — so nothing is checked and nothing
is invented. Both are recorded rather than guessed at.

A release that does not say what resolution it is is refused, and the reason says so rather than
naming a resolution it never claimed. The same choice as the codec tag, for the same reason: what a
release does not say is where the thing you did not want hides.

The language claims a name can make are in § Release names, and the English-only rule reads them as
follows: a release claiming a language that is not English, and not also claiming English, `MULTi` or
`Dual Audio`, is refused. Subtitles are not audio and never refuse anything.

Quality is one rung, not a ceiling. `1080p` means 1080p — a ceiling reads as generous and behaves as
a downgrade, because the 720p copy is usually posted first.

## Season packs

A pack is taken only when the number of gaps in that season reaches `SeasonPackThreshold` and the
profile allows packs. A pack that is taken answers for every gap in the season it covers; an episode
settled by a pack earlier in the same cycle is not asked about again.

## Settings

| Setting | Default | Note |
| --- | --- | --- |
| `TransfersCron` | `* * * * *` | |
| `FeedCron` | `*/15 * * * *` | |
| `SearchCron` | `0 */6 * * *` | |
| `MaintenanceCron` | `0 4 * * *` | |
| `IncompleteFolder` | — | where downloads land |
| `IntakeFolder` | — | where finished video is staged for the encoder |
| `IncludeSpecials` | false | season 0 |
| `MaximumResolution` | `1080p` | one rung, not a ceiling |
| `Codec` | `any` | |
| `RequireCodecTag` | true when a codec is named | an untagged release is where the unwanted codec hides |
| `EnglishOnly` | true | |
| `ExcludeTerms` | empty | |
| `MinimumSeeders` | 2 | judged on the copy, never on a name |
| `AllowSeasonPacks` | true | |
| `SeasonPackThreshold` | 3 | gaps needed before a pack is worth its bytes |
| `MaxSearchAttempts` | 3 | before an episode goes `Unavailable` |
| `MaxConcurrentDownloads` | 5 | |
| `DefaultTrackers` | **empty, then learned** | every tracker the plugin comes across, no duplicates, attached to every grab |
| `Indexers` | empty | the owner's own — see `docs/05-sources.md` |
| `PrivateTrackers` | empty | the owner's own |
| `DisabledDefaultSources` | empty | shipped sources the owner switched off |
| `ListenPort` | 51413 | TCP and UDP |
| `PortMapping` | on | UPnP IGD, then NAT-PMP |
| `MaxDownloadRate` | 0 | bytes/s, 0 is unlimited |
| `MaxUploadRate` | 0 | |
| `SeedRatio` | 1.0 | |
| `SeedHours` | 48 | whichever comes first |
| `StallMinutes` | 30 | no progress **and** no peers |
| `MetadataTimeoutMinutes` | 5 | |
| `ResumeIntervalSeconds` | 60 | named as `ResumeInterval` in `docs/06`; the number is `S5-12`'s |
| `Encryption` | allowed | not required |
| `DryRun` | off | decide but hand nothing to the client |

This table said `DefaultTrackers` was "a shipped list" and no document anywhere said which trackers
were in it. It ships empty until the owner chooses: announcing what is being downloaded to hosts
nobody picked is not a default to invent. `S5-04` and `S6-01` need it filled.

**Secrets are not in this table's shape.** A private tracker's passkey and an indexer's API key are
never fields on the settings object: that object is serialised whole into the host's configuration
file in plaintext. They live in `IPluginSecretStore` under `tracker:{id}:passkey` and
`indexer:{id}:apikey`, and the settings hold an announce URL carrying `{passkey}` where the secret
goes — which is what lets a page show the address without the secret being in it to show.

## Storage schema

SQLite. `PRAGMA user_version` carries the version; migrations in `Storage/Migrations/NNN-name.sql`
run in order at startup.

```sql
-- derived from the library on every maintenance pass. never authoritative.
CREATE TABLE episodes (
    show_id        INTEGER NOT NULL,
    season         INTEGER NOT NULL,
    episode        INTEGER NOT NULL,
    show_title     TEXT    NOT NULL,
    show_year      INTEGER NULL,
    library_type   TEXT    NOT NULL,          -- tv | anime
    absolute       INTEGER NULL,              -- anime only
    episode_title  TEXT    NULL,
    air_date       TEXT    NULL,
    state          TEXT    NOT NULL,          -- notaired | missing | unavailable
    attempts       INTEGER NOT NULL DEFAULT 0,
    last_search_at TEXT    NULL,
    PRIMARY KEY (show_id, season, episode)
);
CREATE INDEX episodes_state ON episodes (state, last_search_at);

CREATE TABLE grabs (
    id            INTEGER PRIMARY KEY,
    show_id       INTEGER NOT NULL,
    season        INTEGER NOT NULL,
    episode       INTEGER NOT NULL,
    release_title TEXT    NOT NULL,
    info_hash     TEXT    NULL,
    source        TEXT    NOT NULL,
    magnet        TEXT    NULL,
    grabbed_at    TEXT    NOT NULL,
    state         TEXT    NOT NULL,           -- grabbed | downloading | done | failed | paused
    covers        TEXT    NOT NULL            -- json array of season/episode this answers for
);
CREATE INDEX grabs_hash ON grabs (info_hash);

CREATE TABLE source_reports (
    name        TEXT PRIMARY KEY,
    at          TEXT NOT NULL,
    rows        INTEGER NOT NULL,
    refusal     TEXT NULL,
    duration_ms INTEGER NOT NULL
);

CREATE TABLE blacklist (
    key    TEXT PRIMARY KEY,                  -- normalised title, or info hash
    reason TEXT NOT NULL,
    at     TEXT NOT NULL,
    until  TEXT NULL
);

CREATE TABLE history (
    id            INTEGER PRIMARY KEY,
    at            TEXT NOT NULL,
    event         TEXT NOT NULL,              -- grabbed | decided | skipped | failed | dispatched | allowed
    show_id       INTEGER NULL,
    season        INTEGER NULL,
    episode       INTEGER NULL,
    show_title    TEXT NULL,
    release_title TEXT NULL,
    source        TEXT NULL,
    detail        TEXT NULL
);
CREATE INDEX history_at ON history (at DESC);

CREATE TABLE name_pool (
    normalised TEXT NOT NULL,                 -- show+slot key
    title      TEXT NOT NULL,
    source     TEXT NOT NULL,
    seen_at    TEXT NOT NULL,
    PRIMARY KEY (normalised, title)
);
CREATE INDEX name_pool_seen ON name_pool (seen_at);
```

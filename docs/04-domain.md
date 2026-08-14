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
| Season/episode | `S03E06` | absolute, or `S02E13` when present | `137` is an episode, `1080` is not — a bare number is only an episode if not followed by `p` |
| Version | — | `v2`, `v3` | a `v2` supersedes the `v1` of the same episode |
| Quality | `1080p` | `(1080p)`, `[1080p]` | brackets |
| Codec | `H264`, `x264`, `H.264`, `AVC` | same | accept `264`/`265` without a prefix; `H.265` has a dot inside |
| Group | after the last `-` | inside the leading `[...]` | a scene title is full of dashes; the group is after the *last* one and contains no dots |
| Language | `MULTi`, `VOSTFR`, `Dual.Audio` | `[Multiple Subtitle]`, `Dual Audio` | |
| Pack | `S01` with no `E` | `01~12`, `Batch`, `Complete` | a pack answers for every gap in the season it covers |

`TitleMatcher.Matches`: normalise both sides (lowercase, punctuation to spaces, collapse), then the
release title must **begin with** the show title and the slot must match. Beginning with, not
containing — *A Bloody Lucky Day* contains *Lucky* and is a different programme.

## The profile: where each rule applies

| Rule | On the **name** | On the **copy** |
| --- | --- | --- |
| Title matches the show | ✅ | |
| Season and episode match | ✅ | |
| Resolution on the ladder | ✅ | |
| Codec, and codec tag required | ✅ | |
| Language | ✅ | |
| Blocked group | ✅ | |
| Forbidden terms | ✅ | |
| Season pack allowed | ✅ | |
| Blacklisted title or hash | ✅ | ✅ |
| **Seeders at or above the minimum** | | ✅ |
| **Size within bounds** | | ✅ |

A copy nobody is seeding is refused with a history line naming the site and the count.

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
| `DefaultTrackers` | a shipped list | attached to every grab |
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
| `Encryption` | allowed | not required |
| `DryRun` | off | decide but hand nothing to the client |

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
    event         TEXT NOT NULL,              -- grabbed | skipped | failed | dispatched | allowed
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

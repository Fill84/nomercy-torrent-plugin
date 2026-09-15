# Domain

Where the episode list comes from is `docs/02-library.md`. This is what the plugin does with it.

## Episode states

| State | Meaning | Shown as |
| --- | --- | --- |
| `NotAired` | `AirDate` is null or in the future | *waiting to air* — never searched, never counted as missing |
| `Missing` | aired, no file | *looking*, however many times it has been searched |

There used to be a third state, `Unavailable`: asked `MaxSearchAttempts` times, nothing acceptable
exists. It never held — every maintenance pass re-derives the list from the library, so the very next
refresh put the episode back to `Missing` and counted another attempt regardless. The owner's Freak
Brothers episodes stood at 67–69 attempts on 11 September 2026 while the setting read 3. Asked whether
it should hold for a time instead, the owner's decision of 12 September 2026 was to drop it outright:
every gap is searched on every run, for ever. Migration `009` rewrites what `Unavailable` left on the
owner's own disk, back to `Missing`.

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
| Pack | `S01` with no `E` | `01~12`, `Batch`, `Complete` | a pack names no episode, so it is not searched for |

**A release name names one episode of one show by its season and episode number**
(`docs/specs/release-names.md`). A season pack, a run of episodes and an absolute-numbered anime post
name no episode, and a run does not search for them.

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

## A show's settings: where each rule applies

There is no global profile. Since 15 September 2026 quality, codec, specials, wishes, musts and
forbidden are set per show and per library on the overview (`docs/specs/show-list.md`), and no show
setting is applied anywhere except to release names (`docs/specs/release-names.md`).

| Rule | On the **name** | On the **indexer row** |
| --- | --- | --- |
| Title matches the show | ✅ | |
| Season and episode match — one episode | ✅ | |
| Resolution is the show's quality | ✅ | |
| Codec is the show's codec | ✅ | |
| Every must tag | ✅ | |
| No forbidden tag | ✅ | |
| Wishes carried — which group it is searched in | ✅ | |
| Title is the release name searched for | | ✅ |
| Release or hash still refused | ✅ | ✅ |
| **Size within bounds** | | ✅ |

An indexer row is not judged against the show's settings (`docs/specs/indexer-search.md`).

**There is no seeder gate.** The owner's decision, 12 September 2026: there is no threshold, download
what is found — an episode taken the moment it airs has no crowd behind it yet, and refusing it for
that would refuse the exact case worth downloading for. The count is still read off every row, still
shown, and still decides which of two copies wins when `ReleaseDecider` ranks them, but it never
refuses one outright. A copy nobody is seeding still starts; the stall rule ends it after
`StallMinutes` with no progress **and** no peers, which is the rule that already existed for it.
Since 15 September 2026 the seeder count decides nothing at all: `ReleaseDecider` is gone, and the
winner is the torrent the most indexers list (`docs/specs/indexer-search.md`).

**A blocked group has no list of its own.** A group is a tag like any other word a release name
carries, so a group put in a show's or a library's forbidden list refuses every name carrying it.
*Size within bounds* has no bounds — no setting names a minimum or a maximum — so nothing is checked
and nothing is invented. Both are recorded rather than guessed at.

A release name that does not say what resolution it is is refused, and the reason says so rather than
naming a resolution it never claimed: what a release does not say is where the thing you did not want
hides.

**English only means English only, and `MULTi` is not English.** Corrected 22 August 2026, from the
owner's own working tool. Any foreign-audio marker on the name refuses it — **even beside an English
one**: `ITA.ENG` carries the English audio and the Italian together, and `MULTI` carries several. The
rule this replaced counted both as English, and it is how
`Silo.S03E07.MULTI.1080p.WEB.H264-HiggsBoson` was taken for an owner whose plain copy was sitting
beside it.

The markers are read off the name as written rather than off a parsed field, whole words only, and
the list is that tool's — fifty of them, and deliberately without `IT`, `ES` or `DE`, which are
ordinary English words or common substrings. Subtitles are not audio and never refuse anything.

Since 15 September 2026 there is no English-only setting. A language is a tag: `DUAL`, `VOSTFR` or
any other language tag goes into a show's or a library's wishes, musts or forbidden
(`docs/specs/show-list.md`).

Quality is one resolution, not a ceiling. `1080p` means 1080p — a ceiling reads as generous and
behaves as a downgrade, because the 720p copy is usually posted first.

## Season packs

A release name without an episode number, such as a season pack, is not searched for
(`docs/specs/release-names.md`).

From the owner's rule of 12 September 2026 until 15 September 2026, a pack was an ordinary copy:
judged by the same rules as a single episode, with no threshold and no switch to refuse it outright,
and a pack that was taken answered for every gap in the season it covered.

## Settings

| Setting | Default | Note |
| --- | --- | --- |
| `Cadences.Cycle` | `0 * * * *` | how often a cycle starts when nothing else starts one; never more often than every 15 minutes |
| `IncompleteFolder` | — | where downloads land |
| `IntakeFolder` | — | where finished video is staged for the encoder |
| `MaxConcurrentDownloads` | 5 | |
| `DefaultTrackers` | **empty, then learned** | every tracker the plugin comes across, no duplicates, attached to every grab |
| `Indexers` | empty | the owner's own — see `docs/05-sources.md` |
| `PrivateTrackers` | empty | the owner's own |
| `DisabledDefaultSources` | empty | shipped sources the owner switched off |
| `ListenPort` | 6881 | TCP and UDP. 6881-6889 is the BitTorrent default; a file that names a port keeps it |
| `MaxDownloadRate` | 0 | bytes/s, 0 is unlimited |
| `MaxUploadRate` | 0 | |
| `SeedRatio` | 1.0 | |
| `SeedHours` | 48 | whichever comes first |
| `StallMinutes` | 30 | no progress **and** no peers |
| `MetadataTimeoutMinutes` | 5 | |
| `ResumeIntervalSeconds` | 60 | named as `ResumeInterval` in `docs/06`; the number is `S5-12`'s |
| `Encryption` | allowed | not required |

**Quality, codec, specials and tags are not settings of the plugin.** The global profile —
`IncludeSpecials`, `MaximumResolution`, `Codec`, `RequireCodecTag`, `EnglishOnly`, `ExcludeTerms` —
is gone since 15 September 2026, dropped from `config.json` on the first start without being carried
over. They are set per show and per library on the overview (`docs/specs/show-list.md`) and stored in
`show_settings` and `library_preferences` (migration `013`). A show nobody has switched on and saved
is off; a library's codec is `any` and its specials off until the owner chooses otherwise.

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
    state          TEXT    NOT NULL,          -- notaired | missing
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

-- name_pool is created by 001 and dropped by 014 (S13-05): a run reads the name sources' feeds and
-- asks their searches directly, so no release name is kept between runs (docs/specs/release-names.md).

-- show_settings and library_preferences are created by 013: one row per show and one per library,
-- replacing the global profile (docs/specs/show-list.md).

-- grabs.folder is added by 008 for the second copy of a release under two hashes. No new grab gets a
-- folder of its own since 15 September 2026 (only the winner is offered); the column is still read
-- for downloads an earlier version put in one.
```

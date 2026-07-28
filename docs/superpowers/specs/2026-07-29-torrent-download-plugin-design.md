# NoMercy Torrent Download Plugin — Design Spec

**Date:** 2026-07-29
**Status:** Approved. Ready for implementation planning.
**Repo:** `nomercy-torrent-plugin` (new, empty)
**Target host:** [`NoMercy-Entertainment/nomercy-media-server`](https://github.com/NoMercy-Entertainment/nomercy-media-server) branch `dev`

---

## 1. Summary

A NoMercy MediaServer plugin that keeps a TV library complete without supervision. It reads the
server's library to work out which episodes are missing, searches configured indexers for them,
picks one release per episode against a user-defined quality/language/group profile, pushes it to a
torrent client, tracks the download to completion, and hands the finished file to the server's
existing import pipeline. Downloads can also be added and removed by hand, by magnet link or
`.torrent` file, from a dashboard panel.

The plugin does not encode, import, rename or scan. Those are the server's job and it already does
them well. The plugin's job ends when a complete video file has been placed in the server's intake
location.

---

## 2. Reference material

| Source | What it gave us |
| --- | --- |
| `F:\DevProjects\torrent-feed` | The proven prior art. A mature, tested Python implementation of this exact problem: three-tier scheduler, release matching, two-site resolver with payload verification, lazy magnet resolution with caching, per-user client routing, reject tracking, live transfer listing. Its `matcher.py` in particular encodes hard-won rules that are being ported, not reinvented. |
| `F:\DevProjects\NoMercyEntertainment-Developement\nomercy-radiostation-plugin` | The plugin project layout, `plugin.json` shape, and the Forgejo CI pattern that packs `NoMercy.Events` + `NoMercy.Plugins.Abstractions` from server source into a local NuGet feed. |
| `nomercy-media-server@dev` source | Ground truth on the loader, capabilities, consent, ALC isolation, and the network allowlist. |
| NoMercy Plugin Platform design spec + phase plans (2026-07-18) | The target contract: `IUiPlugin`, `PluginControllerBase`, `PluginHub`, the declarative component vocabulary, and the phased build order. |
| Plugin platform integration brief (gist, 2026-07-28) | Current wired-vs-dead state, auth model, conventions, and known defects. |
| SiCKRAGE | Referenced as prior art. Not used — `torrent-feed` is closer to the problem and far smaller. |

---

## 3. Goals and non-goals

### Goals

1. Derive the wanted-episode list from the server's own library, not a hand-maintained watchlist.
2. Find and grab the correct release per episode, judged against a user-defined release profile
   covering quality, codec, language (including dual-audio), release group and size.
3. Track every download to completion and hand the result to the server's import pipeline.
4. Let a user add a download by magnet or `.torrent`, and remove one with or without its files.
5. Recover from failure without supervision: stalls, dead torrents and wrong releases are detected,
   blacklisted and replaced.
6. Detect and grab quality upgrades against a cutoff. Replacing the superseded file is deferred
   until a capability exists to gate it (§8.4).
7. Support season packs and full back-catalogue backfill.
8. Offer interactive per-episode search showing why each candidate was accepted or rejected.
9. Work with more than one torrent client and more than one indexer protocol.

### Non-goals

- **Encoding, importing, renaming or library scanning.** The server owns these.
- **Movies.** TV only in v1. The architecture does not preclude it; the scope does.
- **Usenet.** Torrents only.
- **Running an indexer.** The plugin consumes RSS, Torznab and two site scrapers. It is not a tracker.
- **Bypassing the trust model.** Where a capability does not exist to gate a dangerous operation,
  the operation is not shipped (see §8.4, upgrade-replace).

---

## 4. Platform ground truth

Verified against `nomercy-media-server@dev` source, not against the spec.

### Live today

- Plugin discovery, manifest parsing, ABI gating (`PluginAbi.Current = 10.0`, major must match),
  checksum verification, collectible `PluginLoadContext`, DI contribution, lifecycle state machine.
- `IPluginContext`: `EventBus`, `Services`, `Logger`, `DataFolderPath`, `Configuration`, `HttpClient`.
- `IScheduledTaskPlugin` — wired through `PluginCronRegistrar` → `PluginCronExecutor` →
  `CronWorker`, `JobName = plugin:{id}`.
- `IEncoderPlugin` and `IAuthPlugin` — wired.
- `PluginCapabilities` parsing: `hooks[]`, `network.hosts[]`, `ui.mounts[]`, `rest`, `ws`.
- `PluginConsentService` — baseline is `mediaSource`/`metadata`/`ui`. Declaring `rest`, `ws` or any
  `network` block makes a plugin **elevated**, and elevated plugins cannot auto-enable.
- `PluginNetworkAllowlistHandler` — enforces `capabilities.network.hosts` on the context
  `HttpClient`. Globs compile as `Regex.Escape(host).Replace("\\*", "[^.]+")` anchored `^...$`.
- `LibraryFileWatcher` / `FolderWatcher` / `FileCreatedEvent` — the import trigger this plugin relies on.

### Not built

`IUiPlugin`, `PluginView`, `PluginComponent`, `PluginActionIntent`, `PluginNavEntry`,
`PluginControllerBase`, `PluginHub`, `IPluginHubHandler`, `IPluginHubContext`,
`GET /api/plugins/{id}/view`, `GET /api/v1/plugins/ui`,
`POST /api/v1/dashboard/plugins/{id}/consent`, `IPluginManager.GetPluginInfo(Guid)`.
`IMediaSourcePlugin` and `IMetadataPlugin` have zero call sites.

### Assembly load context, the rule that shapes everything

`PluginLoadContext.Load` returns `null` — meaning *fall back to the host's default context, and
share type identity* — in **two** cases:

1. The assembly name is in `PluginHostOptions.DefaultSharedAssemblies`.
2. `AssemblyDependencyResolver` cannot resolve it from the plugin's own output folder.

Case 2 is the usable one. Referencing a host assembly with `ExcludeAssets="runtime"` (PackageReference)
or `<Private>false</Private>` (ProjectReference) keeps it out of the plugin's output and `deps.json`,
so the resolver fails, `Load` returns null, and the plugin binds the host's copy. This is the standard
.NET plugin pattern. It is implicit and version-fragile, which is why the platform-level fix belongs
upstream — but it works today and it is what makes `IDataProtector` reachable (§10.2).

`PluginHostOptions.SharedAssemblies` exists but is **completely unwired**: nothing binds it from
configuration and no construction site passes a custom set. Filed as upstream issue #14.

---

## 5. Architecture

### 5.1 Projects

```
nomercy-torrent-plugin.sln
├── NoMercy.Plugin.TorrentDownloader.Core     ← pure domain. No Abstractions reference.
├── NoMercy.Plugin.TorrentDownloader          ← plugin shell. The only project touching the host.
└── NoMercy.Plugin.TorrentDownloader.Tests    ← xUnit + FluentAssertions over Core.
```

`Core` deliberately has **no reference to `NoMercy.Plugins.Abstractions`**. Consequences:

- It builds and tests without cloning the media server, so the CI cost of the abstractions-packing
  dance is paid once, by the shell, not by the test loop.
- The matching, scoring and client logic — the part that is subtle and easy to get wrong — is
  unit-testable in isolation. This is the property that makes `torrent-feed`'s logic trustworthy and
  it is a requirement here, not a nicety.
- Stage 0 of the build order (§13) has zero platform dependency and can proceed while upstream work
  is outstanding.

### 5.2 Core modules

| Module | Responsibility |
| --- | --- |
| `Releases/` | `ReleaseInfo` record; `ReleaseNameParser` (season, episode, quality, codec, audio/language tags, release group, PROPER/REPACK); `TitleMatcher` |
| `Profiles/` | `ReleaseProfile`, `QualityLadder`, `LanguageProfile`, `GroupPreference`; `ReleaseFilter` (hard accept/reject with reasons) and `ReleaseScorer` (soft ranking) |
| `Indexers/` | `IIndexer` and implementations; `IndexerAggregator`; `FlareSolverrClient` |
| `Clients/` | `ITorrentClient` and implementations |
| `Store/` | Plugin-owned SQLite |
| `Engine/` | `WantedEpisodeCalculator`, `CycleScheduler`, `SearchOrchestrator`, `GrabService`, `TransferTracker`, `RecoveryService` |
| `Ports/` | `ILibraryQuery`, `IClock`, `ISecretProtector`, `IPluginEvents` — everything the shell must supply |

### 5.3 The library port

```csharp
public interface ILibraryQuery
{
    Task<IReadOnlyList<LibraryShow>> GetShowsAsync(CancellationToken ct);
    Task<IReadOnlyList<LibraryEpisode>> GetEpisodesAsync(int showId, CancellationToken ct);
    Task<IReadOnlyList<LibraryFile>> GetFilesAsync(int showId, CancellationToken ct);
    Task<string?> GetShowFolderAsync(int showId, CancellationToken ct);
}
```

`LibraryShow`, `LibraryEpisode` and `LibraryFile` are plugin-owned DTOs carrying only what the engine
needs: identifiers, titles, season/episode numbers, air dates, file paths and resolved quality.

**The implementation is deliberately not decided inside `Core`.** The shell supplies it. The intended
implementation is a narrow read-only query contract exposed by the server in
`NoMercy.Plugins.Abstractions` (upstream issue #18). A REST-backed adapter against the server's own API
is the fallback if that contract is slow to land.

Making `MediaContext` a shared assembly was considered and **rejected**: it would turn the EF model
into public plugin ABI, so every migration becomes a potential plugin break, which collides head-on
with the never-break-self-hosted-users rule. A narrow read-only contract is the correct boundary and
it is the one this port already describes.

### 5.4 Shell responsibilities

The shell implements `IPlugin`, `IScheduledTaskPlugin`, `IUiPlugin` and `IPluginHubHandler`, ships
`PluginControllerBase` controllers, and supplies the `Core` ports. It contains no domain logic.

---

## 6. Persistence

**Plugin-owned SQLite at `{DataFolderPath}/torrents.db`**, accessed through `Microsoft.Data.Sqlite`
and `Dapper`, with a hand-rolled `schema_version` table driving forward-only migrations. This mirrors
`torrent-feed/db.py`.

EF Core was considered and rejected: it drags a large dependency tree into the plugin's ALC for a
single-file store that never crosses the host boundary. `IPluginConfiguration` was considered and
rejected for anything but settings — it is whole-object JSON get/save, so it cannot hold grab rows.

| Table | Purpose |
| --- | --- |
| `settings` | Single-row plugin configuration |
| `monitored_shows` | Per-show monitoring flag and profile override |
| `release_profiles` | Quality ladder, cutoff, language profile, group preferences, term rules |
| `indexers` | Configured indexers, protocol, credentials reference, priority, enabled |
| `download_clients` | Configured clients, protocol, credentials reference, category, incomplete and intake paths |
| `grabs` | One row per grabbed release: episode slot, infohash, title, client, status, timestamps |
| `blacklist` | Releases rejected or failed for a given episode, with reason and expiry |
| `magnet_cache` | Resolved magnets keyed by indexer torrent id |
| `activity_log` | Append-only event log surfaced in the panel |
| `cycle_state` | Next-due timestamp per cycle |

Secrets are never stored in plaintext — see §10.2.

---

## 7. The core loop

Six stages. Each is a separate `Core` service with one job.

### 7.1 Wanted

`WantedEpisodeCalculator` reads `ILibraryQuery` and emits:

```csharp
public record WantedEpisode(
    int ShowId,
    string ShowTitle,
    int Season,
    int Episode,
    DateTimeOffset? AirDate,
    WantedReason Reason,
    string? CurrentQuality);
```

An episode is wanted when **all** hold:

- its show is monitored;
- it has aired, minus a configurable grace window so same-day slots are not chased before the
  release exists;
- it has no file, **or** it has a file whose quality ranks below the profile's cutoff.

`Reason` is `Missing` or `Upgrade`. Specials (season 0) are excluded unless explicitly enabled.

### 7.2 Discover

Two tiers converging on one decision step. This structure is taken directly from `torrent-feed`,
where it is the difference between catching a release in minutes and catching it in hours.

**Feed tier.** Polls configured RSS and scene feeds, parses each item title, matches it against
monitored shows, and for each hit that is wanted runs a targeted indexer search for that specific
episode. Cheap, fast, and it is how new episodes are caught shortly after release.

**Search tier.** Walks the wanted list and queries every enabled indexer per episode. Slower, and it
is what backfills everything the feed missed or that predates the plugin's installation.

Both tiers hand a candidate list to the same decision step. An indexer failing is survivable: the
aggregator degrades coverage rather than aborting a cycle, and logs the failure.

### 7.3 Decide

`ReleaseFilter` applies hard rules, each rejection carrying a human-readable reason. `ReleaseScorer`
ranks the survivors. Exactly one winner per episode. Full rules in §8.

### 7.4 Grab

1. Resolve the magnet or `.torrent` **lazily** — only for the winning release of an un-grabbed
   episode — and cache the result. The same torrent is never resolved twice.
2. Push to the client that this show's profile routes to, with the configured category and a save
   path in the **incomplete directory** (§7.4.1).
3. Record the grab.

**Invariant: a failed push never marks the episode grabbed.** It is retried on the next cycle. This
is the single most important correctness property in the system and it is carried over verbatim from
`torrent-feed`.

#### 7.4.1 Where downloads land

Torrents download to an **incomplete directory that is outside every watched path**, and the finished
file is moved into the intake location on completion.

Downloading directly into a watched library folder is wrong in three separate ways, and it is worth
naming all three because the mistake is easy to make and the failure is silent:

- `LibraryFileWatcher` raises `FileCreatedEvent` on partial files. The server would attempt to import
  a torrent that is 4% downloaded.
- A torrent is not one video file. It is samples, `.nfo` files, subtitle folders, and for scene
  releases a set of RAR parts. Every one of those lands in the watched path and every one of them
  gets offered to the import pipeline.
- It inverts an existing server convention. Intake is `Download/*`; library folders are destinations,
  not sources.

Moving on completion also puts the plugin in control of when import happens. The
`Grabbed → Downloading → Completed` transition becomes the thing that triggers import, rather than the
filesystem racing the download.

The move is a hardlink where the filesystem allows it, so seeding continues from the same bytes, and a
copy otherwise. The plugin picks the largest video file in the torrent, ignoring anything matching the
sample and extras patterns. Both directories are plugin configuration, defaulting to the server's
existing intake convention.

### 7.5 Track

The transfers cycle polls each configured client and joins its torrent list to the `grabs` table on
infohash. Torrents the plugin did not grab are ignored — the panel only offers actions that make
sense for a grab, and deleting somebody else's downloads is not one of them.

State machine: `Grabbed → Downloading → Completed → Moved | Stalled | Failed`.

### 7.6 Complete and recover

**Complete.** On the `Downloading → Completed` transition the plugin selects the video file from the
torrent and moves it into the intake location (§7.4.1). `LibraryFileWatcher` then raises
`FileCreatedEvent` on a whole, single file, and the server's normal import, metadata and encode
pipeline takes over. The plugin publishes a completion event on `IPluginContext.EventBus`, marks the
grab `Moved`, and pushes a `grab.completed` frame to subscribed clients.

A move that fails leaves the grab at `Completed` and is retried on the next transfers cycle, mirroring
the grab invariant: an incomplete handoff is never recorded as a finished one.

The event type needs a sanctioned home — see upstream issue #20. The transport exists; the contract
does not.

**Recover.** A torrent that stalls past a timeout, or sits at zero seeders past a timeout, is removed
from the client, and that specific release is blacklisted for that episode. The next cycle picks the
next-best candidate. A user pressing "wrong release" produces the same outcome by hand.

---

## 8. Release profiles

A profile attaches at library level and is overridable per show. Anime is the case that forces this
to be a real model rather than a handful of flags: absolute numbering, dual audio, and fansub group
preferences all differ from Western TV.

### 8.1 Hard filters

Any one of these rejects a candidate, and the reason string is retained for the interactive-search UI.

| Rule | Detail |
| --- | --- |
| Title scope | The show name must lead the title (with only a trailing year or country code permitted after it) or end exactly where the episode marker begins. Ported from `torrent-feed`'s `name_matches`, including the character-level fallback restricted to the leading position. This is what stops "Lucky" matching "Lucky Hank" or "We.Were.the.Lucky.Ones". |
| Episode slot | The parsed season/episode must equal the slot being resolved. Search engines substring-match, so a query for one episode routinely returns others; without this check a wrong file is grabbed and the real episode is marked done and never fetched. |
| Language required | At least one required audio language present. |
| Language forbidden | No forbidden language tag present. |
| Dual audio | If `RequireDualAudio`, the release must carry a dual-audio marker. |
| Blocked groups | Release group not in the blocklist. |
| Required terms | All required terms/regexes present. |
| Forbidden terms | No forbidden term present. |
| Quality | Parsed quality is in the profile's allowed set. |
| Size | Within the profile's bounds for that quality. |
| Seeders | At or above the profile's floor. |
| Blacklist | Not blacklisted for this episode. |

### 8.2 Soft score

| Signal | Weight shape |
| --- | --- |
| Quality ladder position | Dominant. A quality step outranks everything below it. |
| Exact scene-name match | Strong boost when the candidate title equals the release name the feed announced — the strongest available evidence it is the genuine thing. |
| Preferred release groups | Per-group weight, positive or negative. |
| Preferred terms | `PROPER`, `REPACK`, `Dual Audio`, and user regexes, each weighted. |
| Language preference | Preferred languages score above merely acceptable ones. |
| Codec preference | `h264`, `h265` or `any`. |
| Indexer priority | A trusted tracker wins ties. |
| Seeders | Log-scaled tie-break, so a large seeder difference never outweighs a quality step. |

### 8.3 Language model

`torrent-feed`'s foreign-marker table is reused, but promoted from a boolean reject into a **tag
extractor**: it reports which languages a release carries, and `LanguageProfile` decides.

```csharp
public record LanguageProfile(
    IReadOnlyList<string> Required,
    IReadOnlyList<string> Preferred,
    IReadOnlyList<string> Forbidden,
    bool RequireDualAudio);
```

This is what makes "dual audio anime, prefer this fansub group" expressible rather than hardcoded.

### 8.4 Upgrades and cutoff

A `QualityLadder` is an ordered list with a `CutoffQuality`. An episode whose existing file ranks
below the cutoff is wanted with `Reason = Upgrade`.

**Replacing the old file is not shipped in v1.** When an upgrade imports, the previous file must be
removed or the library holds two copies — but `PluginHookCapability` has six constants
(`mediaSource`, `metadata`, `scheduledTask`, `auth`, `encoder`, `ui`) and **none of them covers
writing or deleting in a user's library**. An ungated file delete is precisely the hole the
trust-on-install model cannot have.

Until a capability exists to gate it (upstream issue #19), upgrade grabs are downloaded and imported,
and the old file is **left in place with a warning surfaced in the panel**. When the capability
lands, the designed behaviour is: wait for the new `VideoFile` to appear via `ILibraryQuery`, then
move — never hard-delete — the old file to a plugin-owned recycle bin with configurable retention,
routed through the storage facade and jailed to library roots.

### 8.5 Season packs

When a configurable number of episodes in a season are wanted and a season pack passes the filters,
the pack is grabbed instead of N single episodes. All contained episodes are recorded against the one
grab and marked pending until the pack completes.

---

## 9. Indexers and download clients

### 9.1 `IIndexer`

```csharp
public interface IIndexer
{
    string Name { get; }
    int Priority { get; }
    Task<IReadOnlyList<ReleaseInfo>> SearchAsync(SearchQuery query, CancellationToken ct);
    Task<string> ResolveMagnetAsync(ReleaseInfo release, CancellationToken ct);
}
```

| Implementation | Notes |
| --- | --- |
| `RssIndexer` | Generic RSS/scene feeds. Parsed with `System.Xml.Linq`, no feed library. Covers the SCNSRC-style scene feed that drives the discovery tier. |
| `TorznabIndexer` | Torznab/Newznab. One protocol, and Jackett or Prowlarr then supplies hundreds of trackers without further work here. |
| `TorrentBayIndexer` | Ported from `torrent-feed`, including the FlareSolverr path for Cloudflare. |
| `LimeTorrentsIndexer` | Ported, including the payload verification that catches mislabelled contents before a grab. |

`IndexerAggregator` fans out, merges, deduplicates by infohash and normalised title, and survives
individual failures.

**Rate limiting is part of the aggregator, not an afterthought.** A six-hour backfill queries every
enabled indexer once per wanted episode, which for a library of any size is precisely the access
pattern that gets an account banned. Each indexer carries a configurable minimum request interval and
a concurrency cap, enforced by the aggregator, with exponential backoff on `429` and `503` and a
circuit breaker that parks an indexer for a cooldown after repeated failures. A parked indexer
degrades coverage exactly like a failing one — the cycle continues without it and the state is visible
in the panel.

Search work is therefore paced rather than parallel-unbounded, and the search cycle is written to be
interruptible so a long backfill yields to the cancellation token promptly (§12.5).

### 9.2 `ITorrentClient`

```csharp
public interface ITorrentClient
{
    string Name { get; }
    Task<bool> TestAsync(CancellationToken ct);
    Task AddMagnetAsync(string magnet, TorrentAddOptions options, CancellationToken ct);
    Task AddTorrentFileAsync(Stream torrent, TorrentAddOptions options, CancellationToken ct);
    Task<IReadOnlyList<TorrentStatus>> ListAsync(CancellationToken ct);
    Task RemoveAsync(IReadOnlyList<string> infoHashes, bool deleteFiles, CancellationToken ct);
}
```

Implementations: `QBittorrentClient` (v2 Web API, ported from `torrent-feed` including the
403-relogin and 409-is-success handling), `TransmissionClient` (RPC with session-id handshake),
`DelugeClient` (JSON-RPC with `WebUtils` auth).

---

## 10. Configuration and secrets

### 10.1 Configuration

**All user configuration lives in the dashboard UI and the database. No environment variables, no
"set X before first run", no bootstrap config file.** This is a hard host rule and it holds here
regardless of how much easier a YAML file would be. The plugin ships with working defaults and every
setting is reachable from its panel.

Structural settings live in `IPluginConfiguration` (whole-object JSON). Operational data lives in the
plugin's SQLite.

### 10.2 Secrets

Torrent-client and indexer credentials are protected with `IDataProtector` before being written to
the plugin's SQLite. `IDataProtectionProvider` is resolved from `IPluginContext.Services`, with
`Microsoft.AspNetCore.DataProtection.Abstractions` referenced `ExcludeAssets="runtime"` so type
identity is shared with the host (§4).

The `credentials` endpoints on `PluginController` are hardcoded to AniDb
(`CredentialManager.Credential("AniDb")` returning `AniDbCredentialsResponseDto`) and cannot be used.
A general plugin secret store is upstream issue #21.

### 10.3 Network access

`capabilities.network.hosts` is a **static list in the manifest**, and its globs compile to
`Regex.Escape(host).Replace("\\*", "[^.]+")` anchored `^...$` — so `*` cannot cross a dot and a bare
`*` matches only a dotless hostname. **There is no allow-all.**

Every host this plugin talks to — indexers, trackers, FlareSolverr, torrent clients — is user
configuration, unknown at package time. The plugin therefore cannot function under a
manifest-fixed list. This is an unsolved platform conflict, not a detail, and it **blocks the Grab
stage entirely** (upstream issue #17).

#### The consent inversion, stated plainly

Declaring `network` makes the plugin elevated and forces a consent prompt. But the host list that
consent covers is effectively meaningless, while the plugin talks to arbitrary user-configured hosts
through its own handler. **The user consents to less than the plugin actually does.** That is a worse
trust posture than declaring nothing, because it looks like it was reviewed.

Since the plugin is self-enforcing anyway, the honest answer is to make the real behaviour auditable
at the UX level rather than hidden behind a manifest that does not describe it:

- The panel shows the **effective host list** — every host the plugin will actually contact, not the
  manifest's.
- Adding a host the user has not already seen requires explicit confirmation in the panel before any
  request goes to it.
- The list is persisted and shown as a standing ledger, not a one-time prompt, so it can be reviewed
  at any point.

That turns "documented bypass" into "bypass with a user-visible ledger", which is a materially
different thing, and it is the only honest version of this until #17 lands. When it does, the ledger
becomes the input to the platform's dynamic allowlist rather than a substitute for it.

Interim behaviour: the plugin routes its own outbound calls through a `DelegatingHandler` enforcing
the **user-configured** host list with identical glob semantics. The plugin is self-restricted and
auditable rather than unrestricted, and swapping to platform enforcement when it exists is one
adapter. This is documented honestly as a bypass of the platform allowlist, because it is one.

---

## 11. Scheduling

`IScheduledTaskPlugin` exposes exactly one `CronExpression` per plugin. The plugin therefore
registers its **fastest** cadence and gates slower work inside the tick — the registered expression
is the ceiling.

`CronExpression => "* * * * *"`. `ExecuteAsync` is a single tick driving `CycleScheduler`, which owns
four independent cadences with next-due timestamps persisted in `cycle_state`, plus a re-entrancy
guard so a long search never overlaps itself.

| Cycle | Default | Work |
| --- | --- | --- |
| `transfers` | 1 min | Poll clients, detect completion and stalls, emit events and hub frames |
| `feed` | 15 min | Poll RSS/scene feeds, resolve named episodes |
| `search` | 6 h | Backfill wanted episodes across indexers |
| `maintenance` | 24 h | Prune magnet cache, rotate the activity log, expire blacklist entries |

All cadences are user-configurable. One minute is the floor, since cron has no sub-minute resolution.
Multi-job registration is upstream issue #24; nothing waits on it.

---

## 12. Control surface

The plugin declares `rest`, `ws`, `network` and `ui`, which makes it **elevated**. It therefore
installs `Disabled` and requires recorded consent before it can be enabled. That is correct
behaviour, and the panel presents "installed, pending consent" as a normal state, not an error.

There is currently **no way to grant that consent**: `POST /api/v1/dashboard/plugins/{id}/consent` is
unbuilt, `IPluginConsentService.GrantConsent` has no HTTP caller, and the dashboard has no
pending-consent state to present. This blocks first run and is the highest-priority upstream ask
(issue #15).

### 12.1 Views (`IUiPlugin`)

| Route | Content |
| --- | --- |
| `/` | Activity: wanted count, live transfers with progress/ETA/speed, recent grabs, errors |
| `/shows` | Monitored shows, per-show profile override, monitoring toggle |
| `/shows/{id}` | Season/episode grid — has-file, wanted, grabbed, downloading; per-episode "search now" |
| `/search` | Interactive search results with per-candidate rejection reasons |
| `/profiles` | Quality ladder, cutoff, language profile, group preferences, term rules |
| `/settings` | Indexers, download clients, cadences, paths |
| `/add` | Add magnet, upload `.torrent` |

The nav mount `section` value is undecided: the vocabulary is undocumented and the only known value
is `"music"` from the radio plugin. Upstream issue #22.

### 12.2 REST (`PluginControllerBase`, `api/plugins/{id}/…`)

`GET /transfers` · `DELETE /transfers/{hash}?deleteFiles=` · `POST /downloads/magnet` ·
`POST /downloads/torrent` · `GET /wanted` · `POST /search/{showId}/{season}/{episode}` ·
`POST /grab` · `POST /rejects` · `GET|PUT /shows/{id}` · `GET|PUT /profiles` · `GET|PUT /settings`

### 12.3 WebSocket (`/pluginHub`)

Pushes `transfer.progress`, `grab.new`, `grab.failed`, `grab.completed`, `cycle.completed`, so the
panel is live without polling.

### 12.4 Component vocabulary gap

The specced vocabulary — `PluginContainer`, `PluginText`, `PluginImage`, `PluginList`, `PluginRow`,
`PluginGrid`, `PluginCard`, `PluginDetail`, `PluginButton`, `PluginForm` (text/number/toggle/select),
`PluginWebView`, `PluginEmptyState`, `PluginSpinner` — does not cover this plugin. It needs a table,
a progress indicator, a status badge, a file field for `.torrent` upload, a checkbox field for bulk
actions, and a destructive-confirm on a button.

These are requested as vocabulary additions rather than solved with the webview escape. A table with
progress bars is not custom UI, and reaching for the escape here would make it the default path for
every real plugin, which defeats the isolation decision the vocabulary exists to serve. Each addition
must land in the server vocabulary, the web component map and the Kotlin sealed set **in the same
PR** — client type drift is silent on both clients. Upstream issue #23.

### 12.5 Lifecycle: disable, uninstall, and coming back

`PluginLoadContext` is collectible and `IPlugin : IDisposable`, so what `Dispose` does is part of the
contract, not an implementation detail.

**On `Dispose` (disable or unload):**

- Stop the cycle scheduler and stop accepting new ticks.
- Cancel in-flight work through the plugin's own `CancellationTokenSource`. The search cycle is the
  long one, so it checks the token between indexers and between episodes rather than only at cycle
  boundaries.
- Wait a bounded period for the current tick to unwind, then give up rather than block unload. A
  plugin that will not let go of a collectible ALC is worse than one that abandons a search.
- Flush and close the SQLite connection.
- **Leave every torrent in the client untouched.** The plugin does not own the user's downloads and a
  disable is not a cancel. A download in flight when the plugin is disabled keeps downloading.

**On re-enable**, the plugin reconciles rather than assuming. Grabs still recorded as `Downloading`
are matched against the client's current torrent list by infohash, and each resolves to exactly one of:

| Found in client as | Resolution |
| --- | --- |
| Still downloading | Resume tracking, no action |
| Complete, never moved | Run the completion path now, including the move |
| Absent | Mark `Failed` with reason `disappeared while plugin was disabled`; the episode returns to wanted and is not blacklisted, because the release was never proven bad |

Reconciliation runs on the first transfers cycle after initialisation, so a restart of the whole
server takes the same path — there is no separate resume mechanism to keep correct.

**On uninstall**, the plugin's `DataFolderPath` and its SQLite go with it, per the platform's normal
behaviour. Torrents already in the client and files already moved into intake are left alone; they
belong to the user and to the server respectively.

---

## 13. Upstream dependencies

Thirteen asks, filed as eleven issues against `nomercy-media-server` — two of them fold into the
existing #14. The plugin is designed so that Stage 0, the majority of the work, depends on none of them.

All filed against `NoMercy-Entertainment/nomercy-media-server` on 2026-07-29.

| Issue | Ask | Blocks |
| --- | --- | --- |
| [#15](https://github.com/NoMercy-Entertainment/nomercy-media-server/issues/15) | **Consent grant path.** `POST /api/v1/dashboard/plugins/{id}/consent` is unbuilt, `GrantConsent` has no HTTP caller, and the dashboard has no pending-consent state. An elevated plugin cannot be enabled at all. | First run |
| [#16](https://github.com/NoMercy-Entertainment/nomercy-media-server/issues/16) | **Phase 2 backend surface** — `GetPluginInfo`, UI contract types, `IUiPlugin`, `PluginHub`, `IPluginHubContext`, ApplicationParts + route convention + capability filter, view/discovery endpoints. | Everything |
| [#17](https://github.com/NoMercy-Entertainment/nomercy-media-server/issues/17) | **Dynamic, consent-gated network allowlist.** Static manifest hosts cannot enumerate user-configured indexers, trackers, FlareSolverr and clients, and there is no allow-all glob. | Grab |
| [#18](https://github.com/NoMercy-Entertainment/nomercy-media-server/issues/18) | **A narrow read-only library query contract in `NoMercy.Plugins.Abstractions`.** Explicitly *not* by sharing `MediaContext`, which would make the EF model public plugin ABI and turn every migration into a potential plugin break. | Missing-episode detection |
| [#19](https://github.com/NoMercy-Entertainment/nomercy-media-server/issues/19) | **A distinct capability for library file write/delete**, with explicit consent, jailing to library roots, and routing through the storage facade rather than raw filesystem calls. Highest-risk ask in this list. | Upgrade-replace |
| [#20](https://github.com/NoMercy-Entertainment/nomercy-media-server/issues/20) | **A sanctioned plugin event contract.** The transport already exists — `IPluginContext.EventBus` is an `IEventBus`. What is missing is an event type host subscribers can bind to, since a plugin-defined type lives in the plugin's ALC. | Completion signalling |
| [#21](https://github.com/NoMercy-Entertainment/nomercy-media-server/issues/21) | **General plugin secret store.** The `credentials` endpoints are hardcoded to AniDb. | Safe client passwords |
| [#22](https://github.com/NoMercy-Entertainment/nomercy-media-server/issues/22) | **Nav-mount `section` vocabulary is undefined.** | Nav entry |
| [#23](https://github.com/NoMercy-Entertainment/nomercy-media-server/issues/23) | **UI vocabulary additions:** table, progress, badge, file field, checkbox field, destructive-confirm. Server vocab + web map + Kotlin sealed set in one PR. | The panel |
| [#24](https://github.com/NoMercy-Entertainment/nomercy-media-server/issues/24) | **Multi-job cron registration.** One `CronExpression` per plugin. Worked around internally. | Nothing |
| [#25](https://github.com/NoMercy-Entertainment/nomercy-media-server/issues/25) | **Phase 3 UI contract + SDK** — vocabulary constants, fluent C# builder, TypeScript mirror, Kotlin mirror. | The panel |

Two further asks are already covered by existing issue
[#14](https://github.com/NoMercy-Entertainment/nomercy-media-server/issues/14) and were not refiled:
`PluginControllerBase` cannot live in `NoMercy.Api` (not a shared assembly, so plugin controllers
cannot bind), and `PluginHubMessage` should not carry `Newtonsoft.Json.Linq.JToken` when every other
Abstractions type uses `System.Text.Json`. #14 also covers the unwired
`PluginHostOptions.SharedAssemblies` seam — a real defect that should be fixed, but explicitly **not**
the mechanism for #18.

---

## 14. Build order

| Stage | Work | Gated by |
| --- | --- | --- |
| **0** | `Core`: release parser, title matcher, filter, scorer, profiles, SQLite store, indexer clients with rate limiting, torrent clients, completion handoff, cycle scheduler. Fully unit-tested. | **Nothing. Starts immediately.** |
| 1 | Shell: `IPlugin`, `IScheduledTaskPlugin`, `ILibraryQuery` adapter, secret protection, manifest, CI | #18, #21 |
| 2 | REST + WS surface | #15, #16, #17, #14 |
| 3 | UI views | #25, #22, #23 |
| 4 | Upgrade-replace with recycle bin | #19 |

Stage 0 is the majority of the work and the part where correctness is hardest, so the upstream
dependencies gate the surface, not the substance.

---

## 15. Testing

`Core` is tested without any host present.

- **Release parsing and matching** — table-driven against real release names, including the
  `torrent-feed` regression cases: "Lucky" vs "Lucky Hank" vs "We.Were.the.Lucky.Ones",
  "Special Ops Lioness S02E01", "Big Brother US S28E08", glued tokens from search highlighting.
- **Filter and scorer** — every hard rule proved to reject with the right reason; scoring proved to
  be ordered correctly, especially that a quality step outranks any seeder difference.
- **Language extraction** — multi-audio tags (`ITA.ENG`), dual-audio markers, foreign episode
  numbering with no language tag (`[Cap.101]`).
- **Indexers** — parsed against recorded fixtures, as `torrent-feed` does. No live network in tests.
- **Clients** — against a stub HTTP handler, including qBittorrent's 403-relogin and 409-as-success.
- **Engine** — the grab invariant gets its own test: a failed push must leave the episode un-grabbed.
  Stall detection, blacklisting and next-best replacement each get one.
- **Completion handoff** — the video file is selected over samples and extras; a failed move leaves the
  grab at `Completed` and is retried; nothing is ever written to a watched path before the torrent
  finishes.
- **Reconciliation** — each of the three re-enable outcomes, including that a torrent which vanished
  while disabled returns the episode to wanted **without** blacklisting the release.
- **Rate limiting** — minimum interval and concurrency cap are honoured under fan-out; `429` triggers
  backoff; repeated failure parks the indexer and the cycle still completes with the others.
- **Cycle scheduler** — due-time arithmetic, the re-entrancy guard, and cancellation mid-search
  unwinding within the bounded window, all driven by a fake clock.

Assertions are on outcomes, never on "it ran".

---

## 16. Build and CI

.NET 10, matching the server. Forgejo Actions, following the radiostation plugin's workflow: clone
`nomercy-media-server@dev`, `dotnet pack` `NoMercy.Events` and `NoMercy.Plugins.Abstractions` into a
local feed, restore and build the shell against it. `Core` and `Tests` build and run without that
step, so the test loop stays fast.

A `v*` tag produces a release with the plugin folder zipped: DLL, dependencies, `plugin.json`,
`README.md`. The manifest declares `targetAbi: "10.0"` — the `dotnet new` template's stale `9.0`
would be refused by `AbiVerificationStage`.

Host conventions are followed in the shell: explicit types never `var`, license header on every file,
CSharpier with an explicit file list, no useless comments, contract-based DI, small single-purpose
files.

---

## 17. Risks

| Risk | Response |
| --- | --- |
| Upstream Phase 2/3 slips | Stage 0 is the majority of the work and is unblocked. The plugin is useful the moment #18 and #17 land, even without a panel. |
| `ExcludeAssets="runtime"` binding is implicit and version-fragile | Confined to two references (data protection, and the library contract if it ships as a package). Both behind ports, both replaceable in one file. |
| Indexer scrapers break when a site changes markup | Expected. `IndexerAggregator` degrades rather than failing, RSS and Torznab are unaffected, and fixtures make a fix a small change. |
| Deleting a user's media file | Not shipped until a capability gates it. Never a hard delete when it is. |
| The plugin bypasses the platform network allowlist in the interim | Self-restricted to a user-configured list with identical semantics, surfaced as a user-visible host ledger (§10.3), and removed the moment #17 lands. |
| Wrong release grabbed and episode marked done | The episode-slot hard filter exists specifically for this, and it is one of the first tests written. |
| Getting an indexer account banned by a backfill | Per-indexer pacing, concurrency cap, backoff and circuit breaker in `IndexerAggregator` (§9.1). Conservative defaults. |
| Partial files or scene extras offered to the import pipeline | Downloads never touch a watched path. Only a selected, complete video file is moved into intake (§7.4.1). |

---

## 18. Open questions

1. The nav-mount `section` value for a downloader (upstream #22).
2. Whether the library query contract (#18) ships as part of `NoMercy.Plugins.Abstractions` or as its
   own package — affects one `PackageReference` in the shell, nothing else.
3. Whether multi-user routing is wanted. `torrent-feed` routes different shows to different clients
   per user. This spec routes per show via the profile, which is the single-owner case. Multi-user is
   additive to `monitored_shows` if it turns out to be needed.

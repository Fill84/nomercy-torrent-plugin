# NoMercy Torrent Downloader

A plugin for the [NoMercy MediaServer](https://github.com/NoMercy-Entertainment/nomercy-media-server)
that keeps a TV library complete without supervision.

It reads the server's library to work out which episodes are missing, searches configured indexers
for them, picks one release per episode against a quality/language/group profile, pushes it to a
torrent client, tracks the download, and hands the finished file to the server's import pipeline.
Downloads can also be added and removed by hand, by magnet link or `.torrent` file.

> **Status: in development.** The domain core and the indexer layer are built and tested. The
> plugin shell — the part that actually loads into the server — is next, and no longer blocked:
> see [Upstream platform work](#upstream-platform-work).

## What works today

`NoMercy.Plugin.TorrentDownloader.Core` — the decision engine, with no dependency on the media
server at all. It builds and tests standalone.

| | |
| --- | --- |
| Release parsing | season/episode, season packs, resolution, source, codec, release group, PROPER/REPACK, language tags |
| Show matching | decides whether a release title belongs to a given show |
| Release profiles | quality ladder with a cutoff, language profile including dual-audio, preferred and blocked groups, term rules, size and seeder bounds |
| Filtering | hard accept/reject, every rejection carrying a reason a user can act on |
| Scoring | soft ranking where a quality step outranks every other signal combined |
| Indexers | `IIndexer` contract, RSS and scene-feed reading, Torznab for Jackett and Prowlarr |
| Rate limiting | per-indexer minimum interval, concurrency cap, exponential backoff on a rate limit, circuit breaker that parks a failing indexer |
| Aggregation | fan-out across indexers, one failing indexer never takes the search down, dedupe by infohash and title preferring the copy that can actually be downloaded |

## Design

Two projects, and the split is the point:

- **`Core`** — pure domain logic. No reference to any NoMercy assembly, no I/O outside the indexer
  clients, no reading of the system clock. It compiles and its tests run without cloning the media
  server, which keeps the test loop fast and the logic honest.
- **The plugin shell** (not yet built) — the only part that touches the host. It implements the
  plugin interfaces and supplies `Core` with its ports.

The full design is in [`docs/superpowers/specs/`](docs/superpowers/specs/), and the implementation
plans in [`docs/superpowers/plans/`](docs/superpowers/plans/). They are written to be read: the
spec records the decisions and the reasons, including the ones that went the other way.

## Tested against real captures, deliberately

`tests/fixtures/` holds real responses captured from live indexers. Parsers are tested against
those rather than hand-written samples.

That is a correction, not a preference. The first stage was built against invented release titles,
and nearly every defect review found came from a case the invented data politely avoided — a show
actually called *Greek* being read as a Greek-language release, a name with a diacritic tokenising
into fragments, an indexer that appends `[eztv.re]` to every title. Real captures do not flatter a
parser.

## Building

Requires the **.NET 10 SDK** (matches the server's target framework).

```bash
dotnet build
dotnet test
```

`Core` and its tests have no external dependency. The plugin shell, once it exists, will need the
server's `NoMercy.Plugins.Abstractions` — see the radiostation plugin's CI for the pattern.

## Upstream platform work

The plugin shell needed twelve platform capabilities the media server did not have. They were filed
as [issues #15–#25](https://github.com/NoMercy-Entertainment/nomercy-media-server/issues) and all
shipped by **v0.1.446** — consent granting, the Phase 2 backend surface, a network allowlist that
can express user-configured hosts, a read-only library contract, capability-gated library writes,
events, a secret store, navigation, UI vocabulary, cron, and the UI SDK.

Two of those decisions are worth recording, because they shaped this plugin:

- The library is read through a **narrow read-only contract**, not the server's EF `MediaContext`.
  Sharing the EF model would have made it a public ABI, and every migration a plugin break.
- Secrets go through the server's secret store and its data protector. **Never an environment
  variable, never plaintext on disk.**

`Core` deliberately depends on none of it, which is why it was built first and why its tests run
without cloning the server.

## Prior art

The matching rules are ported from a working Python prototype, including the ones that only exist
because something broke: the show-name scope rule that stops `Lucky` matching `Lucky Hank`, the
episode-slot check that stops the wrong episode being marked done, and the invariant that a failed
push never marks an episode grabbed.

## Licence

MIT.

# Architecture

## The problem to solve

0.3.4 walked one episode at a time and asked one source at a time, paying every source's politeness
interval serially. Forty-two missing episodes against a name database paced at sixty seconds is
forty-two minutes of a cycle that mostly sleeps, and the next cycle starts before the last one ends.

Three ideas fix it:

1. **One harvest answers for everything.** Feeds are read once per cycle into a pool of release
   names. Most missing episodes are answered from the pool with no extra request.
2. **A question is asked once.** What the pool misses is asked per *show and season*, not per
   episode — six queries instead of forty-two — and memoised for the cycle.
3. **Politeness is per host, parallelism is across hosts.** Every outbound request passes a gate
   keyed by hostname. Every source runs at full speed alongside the others; none is asked faster
   than its catalogue entry allows.

## Stages

```
                  ┌──────────────┐
cadence: feed ───▶│ 1 Harvest    │  all feed sources in parallel, gated per host
                  └──────┬───────┘
                         │ NamePool
                         ▼
                  ┌──────────────┐
cadence: search ─▶│ 2 Name       │  pool first; misses ask the name databases
                  │   resolve    │  once per (show, season), memoised
                  └──────┬───────┘
                         ▼
                  ┌──────────────┐
                  │ 3 Judge the  │  the profile applied to NAMES:
                  │   name       │  slot, quality, codec, language, group, packs
                  └──────┬───────┘
                         ▼
                  ┌──────────────┐
                  │ 4 Find       │  every indexer in parallel, gated per host
                  │              │  merge by info hash, union the trackers
                  └──────┬───────┘
                         ▼
                  ┌──────────────┐
                  │ 5 Judge the  │  the profile applied to COPIES:
                  │   copy       │  seeders, size
                  └──────┬───────┘
                         ▼
                  ┌──────────────┐
                  │ 6 Grab       │  hand to the torrent client, record it
                  └──────┬───────┘
                         ▼
                  ┌──────────────┐
cadence: transfer │ 7 Watch      │  progress, completion, failure, stall
                  └──────┬───────┘
                         ▼
                  ┌──────────────┐
                  │ 8 Hand off   │  stage the video, dispatch the encode job
                  └──────────────┘
```

Stages 2–6 run per episode, concurrently. Stages 1 and 4 fan out per source, concurrently. Nothing
is serial except where a host's gate makes it so.

## HostGate

Every outbound request — HTTP or through the browser — goes through
`HostGate.RunAsync(host, work, ct)`. One gate per hostname, built from the catalogue:

- `minimumIntervalSeconds` — the smallest gap between two requests to that host.
- `maxConcurrent` — how many may be in flight (default 2).

The gate is the only thing that slows anything down. No stage sleeps and no stage knows another
source exists.

It also owns backoff: `429`, `503` or `509` widens that host's interval exponentially and success
narrows it. A refusal that is our own fault — the server not having granted the host — is not
backoff and not failure: it is reported, and the host is skipped for the cycle.

## Degrees of parallelism

| Stage | Concurrency |
| --- | --- |
| Harvest | all feed sources at once |
| Name resolve | `min(8, cores)` episodes |
| Find | all indexers at once, per name |
| Grab | serial — the store and the client are shared, and grabbing is fast |
| Transfers | all in flight at once |

Defaults live in `PipelineOptions`, not as constants scattered through the code.

## Ownership of work

A cycle is owned by the plugin, never by the request that started it. The Run button starts a cycle
and answers immediately; the cycle runs on the plugin's lifetime token. A cadence tick arriving
while its own cadence is still running is dropped, not queued.

## The plugin's own subsystems

Both in-process, both started once when the plugin initialises:

- **Torrent client** — `docs/06-torrent-client.md`. The BitTorrent protocol written in this
  repository, behind `ITorrentEngine` so `Core` never sees it.
- **Challenge solver** — `docs/07-solver.md`. A Chrome the plugin downloads and drives on a hidden
  desktop.

## Storage

SQLite through `Microsoft.Data.Sqlite`, in the plugin's data folder, WAL journal mode.

A JSON file was 0.3.4's store and is the wrong shape: every write rewrites everything, two cadences
writing at once lose each other's work, and asking what is still missing means loading all of it.

Schema in `docs/04-domain.md`. Numbered SQL migrations run in order at startup; the version lives in
`PRAGMA user_version`.

## Observability

Every stage publishes to `IActivityJournal`, which keeps a live snapshot and a bounded history. The
snapshot is pushed over `IPluginHubContext` on change, coalesced to at most one push every 250 ms.
Every page renders from the snapshot; nothing polls.

Built in Sprint 0, before the work it observes. **A stage that cannot be seen does not ship.**

## Project layout

```
src/
  NoMercy.Plugin.TorrentDownloader.Core/     no NoMercy references, no I/O beyond its ports
    Domain/          episodes, releases, profiles, anime numbering
    Naming/          release-name parsing and matching
    Sources/         catalogue, readers, fetch abstraction, host gate
    Pipeline/        harvest, resolve, judge, find, grab, whose show it is
    Ports/           the six interfaces the shell fulfils, and nothing else
    Activity/        the journal
  NoMercy.Plugin.TorrentDownloader.Bittorrent/   the protocol: bencode, peers, pieces, trackers, DHT
  NoMercy.Plugin.TorrentDownloader/          the shell: everything touching the host
    Hosting/         wiring, library adapter, encode dispatch, grants
    Solver/          Chrome, hidden desktop, clearance
    Storage/         SQLite and migrations
    Views/           dashboard and detail pages
    Controllers/     the plugin's endpoints
tests/
  ...Core.Tests/          fast, no network, no host
  ...Bittorrent.Tests/    protocol tests against captured wire bytes
  ...Tests/               shell tests with a fake host
  ...Integration/         real network, excluded from the default run
  fixtures/               real captured pages and wire captures
tools/
  SourceHealth/           walks every source through the real chain
  Capture/                saves a real page into tests/fixtures
scripts/
  fetch-abstractions.*    packs the plugin contract from the media-server checkout
  deploy-to-server.*      copies a build onto a stopped server, verifies every hash
```

`Core` references neither `NoMercy.Plugins.Abstractions` nor the Bittorrent project. That is what
makes the pipeline testable without a server and without a swarm.

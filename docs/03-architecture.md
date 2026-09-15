# Architecture

## The problem to solve

0.3.4 walked one episode at a time and asked one source at a time, paying every source's politeness
interval serially. Forty-two missing episodes against a name database paced at sixty seconds is
forty-two minutes of a cycle that mostly sleeps, and the next cycle starts before the last one ends.

Three ideas fix it:

1. **The feeds answer first.** Every name source's feed is read once, all at the same time, at the
   start of a run, and a feed name is taken for the episode it names. Only an episode no feed named
   is looked up in the name sources' search. Nothing is kept between runs.
2. **A question is asked once.** A question already asked of an indexer during a run is not asked
   of it again in that run (`docs/specs/run.md`).
3. **Politeness is per host, parallelism is across hosts.** Every outbound request passes a gate
   keyed by hostname. Every source runs at full speed alongside the others; none is asked faster
   than its catalogue entry allows.

## Stages

```
                  ┌──────────────┐
trigger ─────────▶│ 1 Feeds      │  every name source's feed at once, gated per host
                  └──────┬───────┘
                         ▼
                  ┌──────────────┐
then ────────────▶│ 2 Names      │  the feed names taken for the episode; none,
                  │              │  so every name source's search, Show SxxEyy
                  └──────┬───────┘
                         ▼
                  ┌──────────────┐
                  │ 3 Judge the  │  the show's settings applied to NAMES:
                  │   name       │  one episode, quality, codec, musts, forbidden
                  └──────┬───────┘
                         ▼
                  ┌──────────────┐
                  │ 4 Find       │  one wish group at a time, most wishes first:
                  │              │  first-choice indexers in order, then the rest
                  │              │  at once; merge by info hash, union the trackers
                  └──────┬───────┘
                         ▼
                  ┌──────────────┐
                  │ 5 Choose     │  the torrent the most indexers list; level,
                  │              │  the one found first; a refused release or hash is out
                  └──────┬───────┘
                         ▼
                  ┌──────────────┐
                  │ 6 Grab       │  hand the winner to the torrent client, record it
                  └──────┬───────┘
                         ▼
                  ┌──────────────┐
events ──────────▶│ 7 Watch      │  completion, failure and stall, each said by the client
                  └──────┬───────┘
                         ▼
                  ┌──────────────┐
                  │ 8 Hand off   │  stage the video, dispatch the encode job
                  └──────┬───────┘
                         ▼
                  ┌──────────────┐
events ──────────▶│ 9 Close      │  the server says the encode ended; nothing
                  │              │  left in hand, so maintenance, and the cycle ends
                  └──────────────┘
```

A trigger is the Run button, a finished library scan or the owner's cadence. Every arrow after it is
an event or a completed task, and nothing on the line is a clock — see `docs/01-plugin.md` § One
cycle, driven by events.

Stages 2–6 run one episode at a time, in order of show, season and episode, and an episode's winner
is offered to the client before the next episode is worked on (`docs/specs/run.md`). Stage 1 fans out
per name source, stage 2's search per name source, and stage 4 across every indexer that is not a
first-choice one, concurrently. The first-choice indexers are asked one after another.

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
| Feeds | every name source's feed at once |
| Episodes | one at a time |
| Names | every name source's search at once, for an episode no feed named |
| Find | first-choice indexers one after another, then every other enabled indexer at once |
| Grab | serial — the store and the client are shared, and grabbing is fast |
| Transfers | all in flight at once |

Defaults live in `PipelineOptions`, not as constants scattered through the code.

## Ownership of work

A cycle is owned by the plugin, never by the request that started it. The Run button starts a cycle
and answers immediately; the cycle runs on the plugin's lifetime token. A trigger arriving while a
cycle is open is added to that cycle — its feed and search run once more — and is neither dropped nor
run beside it.

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
    Domain/          episodes, releases, show settings, anime numbering
    Naming/          release-name parsing and matching
    Sources/         catalogue, readers, fetch abstraction, host gate
    Pipeline/        name sources, judge, wish groups, indexer round, grab, which shows are searched
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

# NoMercy Torrent Downloader — 0.4.0

A NoMercy media-server plugin. Every episode that is missing from a TV or anime library and has
already aired gets downloaded and handed to the encoder, without anybody at the keyboard — and the
owner can see it happening.

A rewrite of 0.3.4, keeping the same plugin id so it upgrades in place. The old plugin at
`../nomercy-torrent-plugin` is reference for what each site does and for what went wrong; no code is
carried over.

## Start here

| Document | Answers |
| --- | --- |
| [CLAUDE.md](CLAUDE.md) | the working agreement, and what "ga door" means |
| [docs/plan/PROGRESS.md](docs/plan/PROGRESS.md) | where the work is right now |
| [docs/plan/SPRINTS.md](docs/plan/SPRINTS.md) | all 47 slices, fully specified |
| [docs/00-goal.md](docs/00-goal.md) | the goal, the chain, the two rules that shape everything |
| [docs/01-plugin.md](docs/01-plugin.md) | what makes this a NoMercy plugin: identity, ABI, cadences, manifest, deploy |
| [docs/02-library.md](docs/02-library.md) | where the show and episode information comes from, and how "missing" is derived |
| [docs/03-architecture.md](docs/03-architecture.md) | the parallel pipeline |
| [docs/04-domain.md](docs/04-domain.md) | release names, the profile, settings, the schema |
| [docs/05-sources.md](docs/05-sources.md) | the seventeen shipped sources, the owner's own, and every trap |
| [docs/06-torrent-client.md](docs/06-torrent-client.md) | the BitTorrent protocol, written here |
| [docs/07-solver.md](docs/07-solver.md) | the challenge solver, on a hidden desktop |
| [docs/08-ui.md](docs/08-ui.md) | the live dashboard, the pages and every action |
| [docs/09-host-contract.md](docs/09-host-contract.md) | grants, secrets, and the encode dispatch |
| [docs/10-known-failures.md](docs/10-known-failures.md) | every fault 0.3.x shipped, and its test |

## The chain

```
libraries → every show in every tv and anime library
          → missing   no video file, air date in the past — backwards as well as forwards
          → names     read every feed and scene database, pick the release name that already
                      meets the profile
          → find      search that full name on every indexer, merge the matches by info hash
          → download  the plugin's own BitTorrent client
          → encode    dispatch the job; the server does the rest
```

## Building

Requires the **.NET 10 SDK**. On this machine the SDK is user-local — use `~/.dotnet/dotnet.exe`,
not the `dotnet` on `PATH`, which is 8.0.

```
scripts/fetch-abstractions.ps1              # packs the plugin contract from the media server (branch dev)
dotnet build -c Release -warnaserror
dotnet test --filter "FullyQualifiedName!~Integration"
dotnet format --verify-no-changes
```

`fetch-abstractions` clones the media server into `_server/` — shallow, sparse, branch `dev` — packs
`NoMercy.Plugins.Abstractions` and `NoMercy.Plugins.Mvc` into `_nupkgs/`, and clears their entries in
the global NuGet cache first, because a repack of the same version number is otherwise ignored and
nothing says so. Run it again after the media server's contract moves; it prints the version it
packed and warns if `NoMercyContractVersion` in `Directory.Build.props` still asks for another one.

### The test filter

`tests/…Integration` talks to the real internet, so **it is excluded from every ordinary run**:

```
dotnet test --filter "FullyQualifiedName!~Integration"
```

The filter matches the fully qualified test name — namespace, class, method — not the project, so
every test in that project lives under a namespace containing `Integration`, and a test in that
assembly asserts they all do. Run the network tests deliberately:

```
dotnet test tests/NoMercy.Plugin.TorrentDownloader.Integration
```

## Checking the sources

```
dotnet run --project tools/SourceHealth
```

Walks every source through the real chain and writes `health/report.md` plus the page each source
returned. Hand both over when something is flagged: a reader is repaired from the page, and fetching
the address again later gets a different page.

## Deploying

**Stop the server first.** A loaded plugin's assembly is held open, so the copy fails and the old
build stays — which looks exactly like a deploy that worked and changed nothing. The script verifies
every file's hash afterwards.

```
scripts/deploy-to-server.ps1 -Build
```

## Licence

MIT.

# NoMercy Torrent Downloader — 0.3.9

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
| [docs/plan/SPRINTS.md](docs/plan/SPRINTS.md) | all 64 slices, fully specified |
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
scripts/fetch-abstractions.ps1              # packs the plugin contract from the media server
dotnet build -c Release -warnaserror
dotnet test
dotnet format --verify-no-changes
```

`fetch-abstractions` clones the media server into `_server/` — shallow, sparse, branch **`master`** —
and packs four projects into `_nupkgs/`: `NoMercy.Plugins.Abstractions`, `NoMercy.Plugins.Mvc`, and
the `NoMercy.Design` and `NoMercy.Events` that the first of those depends on. It clears their entries
in the global NuGet cache first, because a repack of the same version number is otherwise ignored and
nothing says so.

**`master`, never `dev`.** `dev`'s version is pinned at `0.1.404` and never moves, so packing from it
gives a contract older than released servers carry, and the build fails with a `CS0246` naming a type
— which reads like a missing `using` and is really a server too old.

**All four, not the two this repository names.** `NoMercy.Plugins.Abstractions` declares the other two
as dependencies. Packing two worked on a machine whose `_nupkgs` was already warm and failed the
first build on a clean one, complaining about packages nothing here mentions. Run it again after the media server's contract moves; it prints the version it
packed and warns if `NoMercyContractVersion` in `Directory.Build.props` still asks for another one.

### The test filter

Anything that talks to the real internet belongs in `tests/…Integration`, and is left out by naming
it:

```
dotnet test --filter "FullyQualifiedName!~Integration"
```

The filter matches the fully qualified test name — namespace, class, method — not the project, so
every test in that project lives under a namespace containing `Integration`, and a test in that
assembly asserts they all do.

**Plain `dotnet test` runs that project as well**, and both the gate in `CLAUDE.md` and CI use the
plain form. That is safe only for as long as nothing in there needs a network — today the project
holds the namespace check and local discovery, which use no internet. **A test that does needs the
filter, and the run that omits it will fail on a machine with no route out.**

## Checking the sources

```
dotnet run --project tools/SourceHealth
```

Walks every source through the real chain and writes `health/report.md` plus the page each source
returned.

**It exits non-zero when anything is flagged**, so it can be wired into whatever runs it. A check
that cannot fail is a check nobody acts on.

**Hand the report and the page over together when something is flagged.** A reader is repaired from
the page it failed on, and fetching the address again later gets a different page — usually one that
works, which is how a fault of this kind survives being reported. The page is written beside the
report for exactly that reason: it is the evidence, and it has a shelf life of about a day.

It also writes `health/baseline.json`, which is what each source answered with last time. A source
that answers with **fewer rows than last time** is flagged even though it answered: nought rows off a
page covered in releases is a broken reader and says so loudly, and three rows where there were forty
is the same fault with the volume turned down. Judged against the last run rather than a number
written down here, because what a search returns depends on the term and the day.

A source flagged this way once and never again was a real change and the new count is now the
baseline. One flagged every run is a reader that needs looking at — with its page.

## Deploying

**Stop the server first.** A loaded plugin's assembly is held open, so the copy fails and the old
build stays — which looks exactly like a deploy that worked and changed nothing.

```
scripts/deploy-to-server.ps1 -Build
```

The script refuses to copy anything while the server is still running, rather than leaving the hash
check at the end to explain it one file at a time. Files travel as base64 over ssh, and every one has
its hash compared afterwards — that comparison is the only thing that can tell a deploy that worked
from a deploy that quietly did nothing.

It ships **every file the build produced** bar symbols and documentation, plus the native code for
the platform the server runs on, which it asks that machine for. Nothing is listed by hand.

A hand-kept list is what this replaced, and it drifted three times: it missed the protocol assembly,
it missed `sources.json` — the catalogue, read from beside the assembly, so seventeen sources read as
none — and it named six files where the plugin needs seventeen. That last one reached a server and
the plugin simply did not appear in its list, because the host resolves a plugin's dependencies from
beside the plugin, found none, and reported nothing. Tests hold the built output against what the
dependency file names and against what the solution really builds.

Afterwards, `0.3.9` is what the log line says when the plugin wakes: the manifest, the code and the
compiled file all carry the version and a test holds the three together.

## Releasing

Nothing is released by hand. Push a `v*` tag to forgejo and `.forgejo/workflows/build.yml` runs every
gate above, packages the plugin, checks the package, and publishes the release — **to forgejo and to
GitHub, from that one build**, so the two forges carry the same bytes. The notes come from
`docs/releases/<version>.md`, which is written and reviewed here rather than generated from a tag.

Forgejo leads. GitHub runs no workflow of its own.

## Licence

MIT.

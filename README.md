# NoMercy Torrent Downloader

A plugin for the [NoMercy](https://github.com/NoMercy-Entertainment) media server. Every episode
that is missing from a TV or anime library and has already aired gets downloaded and handed to the
server's encoder — without anybody at the keyboard, and with every step visible while it happens.

It carries the same plugin id as 0.3.4, so it upgrades that plugin in place.

## What it does

```
libraries → every show in every tv and anime library, listed on the overview
          → shows     the ones switched on, each with its own settings or its library's
          → missing   no video file, air date in the past — backwards as well as forwards
          → names     read the name sources' feeds; look up any episode they did not name
          → judge     keep the names that meet the show's settings, most wishes first
          → find      ask every indexer for those names, merge by info hash, most indexers wins
          → download  the plugin's own BitTorrent client
          → staged    the finished episode, moved out for the encoder
          → encode    queue an encode job; the server does the rest
```

**One cycle, and each step starts the next.** A cycle is started by the Run button or by the owner's
interval — hourly by default, every 15 minutes at most. A download is staged the moment it finishes,
the encode is asked for straight after, and what became of that encode is asked of the server's own
job queue by the id it handed back. The library having the episode is what says an encode arrived;
nothing is asked for twice and no clock gives up on it. Maintenance runs once nothing is left in hand.
A trigger during a cycle is added to it, never run beside it.

**The owner switches shows on, one by one.** The overview lists every tv and anime library's shows
that have a video file on disk or are switched on, with how many aired episodes each is missing. A
show switched on is searched straight away with its library's settings; a show given settings of its
own follows those instead. A show switched off has nothing searched and nothing downloaded.

**Missing means missing.** An episode of a show switched on that aired two years ago and was never
downloaded is missing in exactly the same way as one that aired last night, and a show that has ended
is precisely the kind with gaps to fill.

**A release name first, a torrent second.** The name sources — PreDB, srrDB, SceneSource and
PreDB.net — have their feeds read at the start of every run, and an episode no feed named is looked up
in their search as `Show SxxEyy`. They answer what a release is called, never who has it. A name
names exactly one episode: a season pack or an absolute-numbered post names none. Every name is
judged against its show's settings — quality, codec, English only, every must tag and no forbidden
one — before any indexer is asked. When no name gives a torrent — none passed, or no indexer has one
for any that did — the indexers are asked for `Show SxxEyy` itself, and a result counts only when it
names that episode and meets the same settings.

**Wishes decide the order.** The names carrying the most wishes are asked first, all together, and
only when they find nothing does the group with one wish fewer get its turn.

**Each name letter for letter, then as words.** `Silo.S03E06.1080p.WEB.H264-CAKES`, dots and dash
as the source wrote it; without its punctuation only where the exact name found nothing. The
first-choice indexers are asked one after another — TorrentBay then LimeTorrents for a show, Nyaa
first for an anime — and then every other enabled indexer together. A row counts only when its title
is the name.

**One torrent, every tracker, and the most indexers wins.** Everything the indexers return with the
same info hash is one torrent, carrying every tracker any of them knew — nothing unannounceable, and
nothing belonging to the owner's own private trackers. The torrent the most indexers list wins; level,
the one found first. Seeders and a site's rating decide nothing. Only the winner is offered to the
client, and the run carries on with the next episode.

**Private trackers are respected.** A private torrent looks for peers on its own tracker and nowhere
else (BEP 27), and seeds to the owner's ratio or hours. A public torrent never uploads. A passkey or
an API key never appears in a page, a log or an error.

## Sites behind a challenge

Some indexers sit behind Cloudflare. When a run starts, every site that really challenges is solved at
once in a single Chrome on a hidden desktop, the cookies are kept, and the browser is closed; the run
then asks everything over plain HTTP. A challenge that turns up halfway through a run opens the browser,
is solved, and the browser closes again. No window ever opens on anybody's desktop.

A question to a site that does not answer, or that stays behind a challenge, is asked at most twice,
the challenge solved again before the second attempt. A site still failing after that sits out the
rest of the run, and the Sources page says why.

## The pages

| Page | Shows |
| --- | --- |
| Overview | the run's status with Run and Stop; per library its preferences and its shows — missing, quality, codec, tags, on or off — with a switch and a settings form per show |
| Activity | each stage's progress, what every episode in flight is waiting on, which names were refused and why, and every question to every indexer |
| Queue | the episodes being searched for, each with Search now, and what is still waiting to air |
| Downloads | progress, rate, peers, seeds, ratio and destination |
| History | what became of a grab — decided, grabbed, dispatched or failed — and why |
| Sources | per site: what it last answered, how long it took, its refusal in its own words, when it is next asked; a switch per shipped site and your own indexers |
| Settings | folders, how often a cycle starts, the torrent client and its port, private trackers and seeding |

An open page updates live, and only when something changed; nothing is pushed while no page is open.
Times are shown as clock times. A number that is not known says what is missing rather than showing
nought.

A refused release name is said on the Activity page while the run is going, and nothing about it is
stored.

## Settings

**What is downloaded is set on the overview, not on the Settings page.** A show's form and a
library's preferences are the same: quality (2160p, 1080p, 720p or 480p), codec, specials, English
only, and lists of wished, required and forbidden tags. A show follows its library for each of them
until it sets its own, and its tag lists are the library's and its own together. A library's codec is
any and its specials and English only are off until changed; its quality has no default, and a show
with no quality on itself or its library has nothing searched.

Everything on the Settings page has a working default, and the page has one Save; expert fields sit
behind **Show advanced**. The shipped sites are not editable, only switchable, on the Sources page,
where you can also add your own indexers. Private trackers are added on the Settings page, and the
seeding settings appear only once one exists. An API key or a passkey is write-only and never shown
again.

## Requirements

- A NoMercy media server on **plugin contract 12**. The server checks this as it loads a plugin and
  refuses anything under its own major outright: a plugin built against an older contract is marked
  malfunctioned with the reason in the log (`ABI '10.0' is incompatible with server ABI 12.0`) and
  appears on no client. An encode is asked for through `IPluginEncoder`, handed over only because the
  manifest names the `encoder` hook, and what became of it is read from the server's job queue.
- **After installing an update while the server runs, restart the server once.** The server serves the
  controllers of the copy it loaded first (media-server #60), so until it is restarted every button
  answers that a restart puts it right. The pages themselves are drawn by the new copy at once.
- A forwarded port for the torrent client, if you want peers to be able to reach you.

## Installing

Every release is on the [releases page](https://github.com/Fill84/nomercy-torrent-plugin/releases)
as `NoMercy.Plugin.TorrentDownloader-<version>.zip`. The plugin's own index, which a server's plugin
catalogue reads, is [`repository.json`](repository.json): every version, its download and its checksum.

By hand: stop the server, unpack the zip into the server's plugins folder (on Windows
`%LOCALAPPDATA%\NoMercy\plugins`), and start it again. A loaded plugin's files are held open, so a copy
made while the server runs does not take.

## Documentation

| Document | Answers |
| --- | --- |
| [docs/00-goal.md](docs/00-goal.md) | the goal, the chain, the two rules that shape everything |
| [docs/01-plugin.md](docs/01-plugin.md) | identity, contract, the cycle and what starts it, manifest, deploy |
| [docs/02-library.md](docs/02-library.md) | where the show and episode information comes from, and how "missing" is worked out |
| [docs/03-architecture.md](docs/03-architecture.md) | the pipeline |
| [docs/04-domain.md](docs/04-domain.md) | release names, a show's settings, settings, the schema |
| [docs/05-sources.md](docs/05-sources.md) | every shipped source and indexer, and every trap |
| [docs/06-torrent-client.md](docs/06-torrent-client.md) | the BitTorrent client |
| [docs/07-solver.md](docs/07-solver.md) | the challenge solver on a hidden desktop |
| [docs/08-ui.md](docs/08-ui.md) | the overview, the pages and every action |
| [docs/09-host-contract.md](docs/09-host-contract.md) | grants, secrets and the encode dispatch |
| [docs/10-known-failures.md](docs/10-known-failures.md) | every fault 0.3.x shipped, and the test that holds it |
| [docs/specs/](docs/specs/) | the owner's requirements: the show list, release names, the indexer search, the pages and a run |
| [docs/releases/](docs/releases/) | what each release changed |
| [docs/plan/PROGRESS.md](docs/plan/PROGRESS.md) | where the work is right now |

## Building

Requires the **.NET 10 SDK**.

```
scripts/fetch-abstractions.ps1              # packs the plugin contract from the media server
dotnet build -c Release -warnaserror
dotnet test
dotnet format --verify-no-changes
```

`fetch-abstractions` clones the media server into `_server/` — shallow, sparse, branch **`dev`** — and
packs the plugin contract into `_nupkgs/`: `NoMercy.PluginSdk.Abstractions` (which carries
`NoMercy.Events` and `NoMercy.Design` inside it) and `NoMercy.PluginSdk.Mvc`, at the contract's own
version, `12.x`. It clears their entries in the global NuGet cache first, because a repack of the same
version number is otherwise ignored and nothing says so.

**`dev`, since contract 12.** The contract is versioned on its own now (`PluginPackageVersion`), so the
frozen server version that once made `dev` unusable for this no longer matters — and `master` sits on a
release from August carrying ABI 11, which the servers this plugin is installed on refuse. The packages
reach nuget.org with a server release, after which the script is a fallback.

Run it again after the media server's contract moves; it prints the version it packed. The build asks
for `12.*` (`NoMercyContractVersion` in `Directory.Build.props`), so a new minor is taken up on the next
restore and a new major is refused until somebody reads what it changed.

The version lives in `Directory.Build.props`, `plugin.json` and `PluginIdentity`, and a test holds the
three together.

### Tests

Anything that talks to the real internet belongs in `tests/…Integration` and is left out by naming it:

```
dotnet test --filter "FullyQualifiedName!~Integration"
```

The filter matches the fully qualified test name, so every test in that project lives under a namespace
containing `Integration`, and a test in that assembly asserts they all do. Plain `dotnet test` runs that
project too, which is safe only while nothing in it needs a network.

Parsers are tested against real captured pages in `tests/fixtures/`, and protocol code against captured
wire bytes — never hand-written samples.

## Checking the sources

```
dotnet run --project tools/SourceHealth
```

Walks every source through the real chain and writes `health/report.md` plus the page each source
returned. It exits non-zero when anything is flagged, including a source that answers with fewer rows
than last time (`health/baseline.json`). **Hand the report and the page over together**: a reader is
repaired from the page it failed on, and fetching the address again later usually gets one that works.

## Deploying to a server

**Stop the server first.** A loaded plugin's assembly is held open, so the copy fails and the old build
stays — which looks exactly like a deploy that worked and changed nothing.

```
scripts/deploy-to-server.ps1 -Build
```

It refuses to copy while the server runs, ships every file the build produced bar symbols and
documentation plus the native code for the server's platform, and compares every file's hash afterwards.

## Releasing

Nothing is released by hand. Pushing a `v*` tag to Forgejo runs `.forgejo/workflows/build.yml`: every
gate above, the package, a check of the package, and the release — **to Forgejo and to GitHub from that
one build**, so both carry the same bytes — and then `repository.json` is brought up to date. The notes
come from `docs/releases/<version>.md`, written and reviewed here rather than generated from a tag.

## Licence

[MIT](LICENSE) © 2026 Phillippe Pelzer.

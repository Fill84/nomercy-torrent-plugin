# Where the work is

Read this first, update it last. Nothing else decides what happens next.

## Current

**Sprint 1 — Library and the missing list**
**Slice `S1-04` · Shows and Queue pages**

Specification: `docs/plan/SPRINTS.md`, section `S1-04`.

## Blocked

Nothing. One thing waits on the owner without blocking anything:

- **Which trackers ship in `DefaultTrackers`.** The specs said "a shipped list" and never said which.
  It ships empty; a grab attaches only the trackers its source supplied. `S5-04` and `S6-01` are the
  slices that want it filled, so there is time. It is the owner's call because it decides which hosts
  learn what this server is downloading.

## Slices

Tick a box only when the whole definition of done in `CLAUDE.md` holds.

### Sprint 0 — Foundation and spine
- [x] `S0-01` Repository, build, gates
- [x] `S0-02` The plugin loads
- [x] `S0-03` The activity journal
- [x] `S0-04` Pushing the snapshot, and the dashboard
- [x] `S0-05` Settings

### Sprint 1 — Library and the missing list
- [x] `S1-01` Reading the library
- [x] `S1-02` The missing list
- [x] `S1-03` Anime numbering
- [ ] `S1-04` Shows and Queue pages

### Sprint 2 — Sources and fetch
- [ ] `S2-01` The catalogue and the host gate
- [ ] `S2-02` Fetching
- [ ] `S2-03` The hidden stage, and Chrome
- [ ] `S2-04` The solver
- [ ] `S2-05` Readers, part one
- [ ] `S2-06` Readers, part two
- [ ] `S2-07` JSON and XML sources, owner sources, and the health tool

### Sprint 3 — Names
- [ ] `S3-01` Parsing release names
- [ ] `S3-02` Harvest
- [ ] `S3-03` Resolving a name for an episode

### Sprint 4 — Find and decide
- [ ] `S4-01` The profile
- [ ] `S4-02` Find
- [ ] `S4-03` Deciding
- [ ] `S4-04` The pipeline end to end

### Sprint 5 — BitTorrent
- [ ] `S5-01` Bencode
- [ ] `S5-02` Torrent metadata and magnets
- [ ] `S5-03` The engine shell and its port
- [ ] `S5-04` Trackers
- [ ] `S5-05` Peer wire
- [ ] `S5-06` Pieces, verification and disk
- [ ] `S5-07` Metadata from peers
- [ ] `S5-08` Encryption
- [ ] `S5-09` DHT
- [ ] `S5-10` Peer exchange and local discovery
- [ ] `S5-11` Rate limits, choking and seeding
- [ ] `S5-12` Resume, recovery, stalls, pause and ports

### Sprint 6 — Grab, staging, dispatch
- [ ] `S6-01` The grab
- [ ] `S6-02` Completion and staging
- [ ] `S6-03` Encode dispatch
- [ ] `S6-04` Downloads page and history

### Sprint 7 — Anime
- [ ] `S7-01` Anime naming
- [ ] `S7-02` Dual-form search
- [ ] `S7-03` Anime end to end

### Sprint 8 — Finish
- [ ] `S8-01` The remaining pages
- [ ] `S8-02` The remaining actions
- [ ] `S8-03` Health automation
- [ ] `S8-04` Hardening
- [ ] `S8-05` Release 0.4.0

## Log

One line per finished slice: the id, what landed, and anything the next slice should know.

- `S0-01` Solution, three source projects and four test projects, all three gates green. `Core` and
  `Bittorrent` reference nothing at all, and a test in each reads its project file and says so — the
  compiled assembly cannot, because the compiler drops a reference nothing uses yet.
  `scripts/fetch-abstractions.ps1`/`.sh` clone the media server into `_server/` and pack the contract
  into `_nupkgs/`. `S0-02` writes the first real code: it can assume the gates bite.
- `S0-02` Identity, manifest, the four cadences, the two nav entries, and eleven tests — each one
  seen to fail against a one-line mutation of the rule it guards. Reading the server to write the
  manifest turned up the ABI: it declares `10.0`, because `10.1` is refused at load. Nothing is
  deployed yet; `S0-03` gives the plugin something to say beyond "awake".
- `S0-03` `IActivityJournal` in `Core/Activity`: one lock over both collections, history bounded at
  500, and a snapshot that is copied rather than wrapped. Failure is a third outcome beside started
  and finished — it clears the in-flight entry as a finish does but carries the reason, or a subject
  vanishes from the page and the journal says nothing about where it went. `S0-04` pushes the
  snapshot over the hub and renders the first real page from it.
- `S0-04` The dashboard is served at `/` and is a pure function of what it is handed — even "now"
  comes from the snapshot, or two clients reading one push would draw different times. `LiveSnapshot`
  coalesces to one push per 250 ms and goes quiet again after a burst. The status bar says "never
  run · next run time not known", both true: no cycle exists until `S4-04`, and no cron is read until
  `S0-05`. `S0-05` adds settings, and with them the first thing the owner can change.
- `S0-05` **Sprint 0 is done.** Every documented setting round-trips through the host's serialiser
  with its documented default. A refused save writes nothing at all. Secrets never enter the settings
  blob and never reach a page: the view is handed key *names*, so it has no value it could render
  even by mistake, and a test proves that from a real stored passkey to every prop of the page.
  `Cron` is written in `Core` and names the field it refused. `S1-01` starts on the library.
- `S1-01` `ILibrary` in `Core/Ports`, `HostLibrary` in the shell, and it maps and nothing else. Both
  corrections are in: libraries are enumerated and shows asked for per library id (**C6**), and
  neither episode count is on the domain `Show` at all (**C7**) — presence is each episode's own
  `HasFile`. `S1-02` derives the missing list from them.
- `S1-02` `MissingRefresh` in Core derives state from the library alone; SQLite holds the result.
  The refresh writes the derived state every time, which is what makes `Unavailable` temporary
  (**B1**), and leaves `attempts` and `last_search_at` untouched — only a recorded search moves them
  (**B2**). No status is consulted because none exists to consult (**B5**). Rows the library no
  longer has are deleted, so presence is the absence of a row. `S1-03` fills in `absolute`.
- `S1-03` `AbsoluteNumbering.Build` from the episode list already fetched, anime only. The number is
  the episode's own plus the lengths of the seasons before it — **not** its position in the list.
  The two agree only while the list is complete, so the first integration test against a sparse
  season caught the running counter I had written and it read 25 where the answer is 37. Both forms
  are now stored, which is what `S3-03` and `S7-02` search under.

## Decisions

Anything decided that the specs did not already say. If a decision contradicts a spec, fix the spec
and note it here.

- The plugin keeps id `1SBQT26FHF98EBRPYVRGD92CZF`, so 0.4.0 is an upgrade of the installed plugin
  rather than a second one.
- The BitTorrent protocol is written in this repository. No third-party torrent library.
- The challenge solver is the plugin's own Chrome on a hidden desktop.
- There is no follow list. Every show in every `tv` and `anime` library is in scope, and every aired
  episode without a file is fetched, however old.
- Show status is not used and not needed: an ended show is exactly the kind with gaps to fill.
- **The manifest declares `targetAbi` `10.0`, not the `10.1` the specs said.** The server's
  `PluginAbi.Current` on `dev` is `10.0` and `AbiVerificationStage` is enforced, so `10.1` is refused
  at load. `docs/01-plugin.md` and `docs/reference/README.md` are corrected. `ManifestTests` asks
  `PluginAbi.IsCompatible` rather than a literal, so this cannot drift again.
- **`DefaultTrackers` ships empty.** `docs/04-domain.md` said "a shipped list" and no document said
  which. Corrected there; the choice is the owner's, and it is under **Blocked** above.
- **Secrets never enter the settings object.** A passkey lives at `tracker:{id}:passkey` and an API
  key at `indexer:{id}:apikey` in `IPluginSecretStore`; the settings carry an announce URL with
  `{passkey}` standing where the secret goes. `docs/04-domain.md` and `docs/08-ui.md` now say so.
- SQLite is `Microsoft.Data.Sqlite` 10.0.11 in the shell project. Migrations are **embedded
  resources**, so a migration that failed to deploy cannot look like a database already up to date.
  `PRAGMA user_version` carries the number; each migration and its version bump share a transaction.
- A test that opens a SQLite file must call `SqliteConnection.ClearAllPools()` before deleting the
  folder, or the pool holds the file open and the cleanup throws.
- xunit 2.9.3's `IAsyncLifetime` returns `Task`, not `ValueTask`.
- `PluginLibraryShow.Folder` and `PluginLibraryEpisode.Title` are **nullable** in the contract;
  `docs/02-library.md` showed both as non-null. The adapter treats a blank folder as none, because an
  empty string is a folder name that resolves to the library root.
- An episode's `AirDate` crosses as a `DateOnly`. The library holds a broadcast day, and the hours
  attached to it are not a time anything aired at — comparing them against "now" would make an
  episode that aired this morning look as though it had not aired yet.
- The plugin implements `IPluginServiceRegistrator` and registers itself, so a controller is handed
  the instance the host loaded rather than constructing a second one with no context.
- Both routes now serve their real page: `/` the dashboard, `/settings` the settings. The
  version-only page `S0-02` served in the meantime is gone.

## Facts, measured

Kept here so no slice re-discovers them.

- The server runs on `beast-unit`; deploy over ssh with `scripts/deploy-to-server.ps1`. **That script
  does not exist until `S8-04` step 6.** Every slice before it that needs the plugin on the server
  deploys by hand: owner stops the server, copy the build over ssh, verify each file's hash, owner
  starts it. Do not read its absence as a missing step in an earlier slice.
- **The owner stops and starts the server.** Never do it.
- The .NET 10 SDK is user-local: use `~/.dotnet/dotnet.exe`. Bare `dotnet` is 8.0 and cannot build
  this.
- The plugin contract is packed from the media-server checkout on branch **`dev`**, not `master`.
  Clear the NuGet cache after repacking the same version.
- The media-server checkout may be a sparse checkout; `git sparse-checkout disable` gets the whole
  tree when something needs looking up.
- Media type (`tv` or `anime`) is the server's classification, from
  `src/NoMercy.MediaProcessing/Shows/MediaTypeClassifier.cs`, Kitsu-backed. A show is already filed
  in the library matching its media type, so the plugin reads `PluginLibrary.Type` and classifies
  nothing itself. `Library.Type` is a free indexed string column with no enum behind it.
- A downloaded episode is dispatched to the show's own `LibraryId`, so it returns to the library of
  its media type.
- `PluginLibraryQuery.GetShowsAsync(null)` returns every show in every library; it only filters when
  a library id is passed.
- `PluginLibraryShow.HaveEpisodeCount` is `Tv.HaveEpisodes` and is zero for shows with hundreds of
  episodes on disk. Use each episode's `HasFile`.
- `Tv.Status`, `Tv.InProduction`, `Tv.OriginCountry` exist in the database but are **not** projected
  into the plugin contract on `dev`. The plugin does not need them.
- `IPluginSystem` has no server-side implementation on `dev`. Not a route to anything.
- Plugin data: `%LOCALAPPDATA%\NoMercy\plugins\data\<pluginId>\`.
- Logs: `%LOCALAPPDATA%\NoMercy\log\run-*.jsonl`, one JSON object per line, with NUL bytes — strip
  with `tr -d '\000'` before reading.
- The plugin loads roughly a minute after the server starts; cadences register only then.
- Approved network grants did not survive a server restart on this machine. Expect to be asked again
  after every deploy.
- The library holds ~25 shows and ~42 missing episodes, including shows that need their year to be
  searchable: Lucky (2026), Sugar (2024), Lioness (2023), Silo (2023).
- The contract packs as **0.1.404** — the media server's own `<Version>` on `dev`.
  `Directory.Build.props` holds it once, as `NoMercyContractVersion`, and `fetch-abstractions` warns
  when the two have drifted apart.
- Packing the contract needs five projects out of the media server: `NoMercy.Plugins.Abstractions`,
  `NoMercy.Plugins.Mvc`, `NoMercy.Events`, `NoMercy.Design`, and `NoMercy.Analyzers`, which every
  project in that repository inherits.
- `dotnet new sln` on the .NET 10 SDK writes the new `.slnx` format unless given `--format sln`.
- `IDE0005` (unused using) refuses to run at build time without `GenerateDocumentationFile`, and
  fails the build saying so. Both that and `NoWarn=CS1591` are in `Directory.Build.props`.
- The style gate bites: a block-scoped namespace, a `var` and a braceless `if` were each seen to fail
  `dotnet build -warnaserror` as `IDE0161`, `IDE0008` and `IDE0011`.
- `PluginManifest.Description` is `required`, so a manifest without one throws on deserialisation.
  The example in `docs/01-plugin.md` omitted it.
- `IPluginContext.Logger` is `Microsoft.Extensions.Logging.ILogger`. `System`, `Player` and
  `LibraryWriter` are nullable with `null` defaults; everything else must be supplied.
- xunit here is 2.9.3, which has no `TestContext.Current.CancellationToken` — pass
  `CancellationToken.None`.
- A `None … CopyToOutputDirectory` in the shell project travels into the output of a project that
  references it, so a test reads the same `plugin.json` that deploys.
- `PluginView.Components` is nullable; `PluginComponent.Items` and `Props` are not.
- `PluginViews.Text` puts its value in `Props["text"]`, and a `caption` or `muted` variant puts it in
  `Props["helperText"]` instead. `TestSupport/Rendered.cs` reads a page by those two keys rather than
  by walking a fixed path into the tree.
- `IPluginHubContext.PushAsync(string type, object? payload)` takes no cancellation token.
- The shell test project uses `Microsoft.Extensions.TimeProvider.Testing` 10.9.0. `FakeTimeProvider`
  runs timer callbacks synchronously inside `Advance`, so a coalesced push can be asserted on the
  line after it with no waiting and no flake.

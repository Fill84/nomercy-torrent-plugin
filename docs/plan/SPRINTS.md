# The plan

Nine sprints, forty-seven slices. Every sprint ends with something that can be judged. Every slice
ends with a green suite and a local commit.

Slice ids are stable. `PROGRESS.md` points at one and nothing else decides what is next.
Every finished slice is committed and pushed; only a release waits for the owner.

**Read first**, always: `CLAUDE.md`, then the documents the slice names.

| Sprint | Ends with |
| --- | --- |
| 0 · Foundation and spine | the plugin loads, has a dashboard, and everything it will do is already visible |
| 1 · Library and the missing list | it knows what is missing, and the pages say so correctly |
| 2 · Sources and fetch | all seventeen sources answer, proven by the health tool |
| 3 · Names | it can name the release it wants for any missing episode |
| 4 · Find and decide | it can say which copy on which site it would take, for every episode |
| 5 · BitTorrent | the plugin's own client downloads a real torrent |
| 6 · Grab, staging, dispatch | a missing episode is downloaded and an encode is queued |
| 7 · Anime | anime works end to end, absolute numbering and all |
| 8 · Finish | the remaining pages and actions, health automation, hardening, 0.4.0 on the server |

---

# Sprint 0 — Foundation and spine

## S0-01 · Repository, build, gates

**Read first:** `docs/03-architecture.md` § Project layout.

**Files**
- `NoMercy.Plugin.TorrentDownloader.sln`
- `Directory.Build.props` — `net10.0`, `LangVersion 13`, `Nullable enable`,
  `TreatWarningsAsErrors true`, `EnforceCodeStyleInBuild true`
- `.editorconfig` — file-scoped namespaces, explicit types (`IDE0008` as error)
- `.gitattributes`
- `src/…Core/…csproj` — no package or project references
- `src/…Bittorrent/…csproj` — no references either
- `src/…TorrentDownloader/…csproj` — references Core, Bittorrent,
  `NoMercy.Plugins.Abstractions`, `NoMercy.Plugins.Mvc`
- `tests/…Core.Tests`, `tests/…Bittorrent.Tests`, `tests/…Tests`, `tests/…Integration`
- `scripts/fetch-abstractions.ps1` / `.sh`, `nuget.config`

**Steps**
1. Create the solution and seven projects; wire the references. `dotnet new sln` writes the new
   `.slnx` format unless told `--format sln`.
2. Write `Directory.Build.props` and `.editorconfig`; introduce one deliberate style violation, see
   the build fail, remove it. `IDE0005` needs `GenerateDocumentationFile`, and says so by failing the
   build; `CS1591` goes in `NoWarn` with it.
3. `fetch-abstractions`: sparse shallow clone of the media server on branch **`dev`**, pack the
   contract into `_nupkgs/`, clear its NuGet cache entry.
4. One real assertion per test project, so the runner is proven.
5. Document the `dotnet test` filter that excludes `*.Integration` in the README.

**Done when:** `dotnet build -c Release -warnaserror` and `dotnet test` are clean, and neither `Core`
nor `Bittorrent` references any NoMercy assembly.

## S0-02 · The plugin loads

**Read first:** `docs/01-plugin.md`.

**Files**
- `src/…/PluginIdentity.cs` — id `1SBQT26FHF98EBRPYVRGD92CZF`, version `0.4.0`
- `src/…/plugin.json` — per `docs/01-plugin.md` § Manifest, `network.hosts` empty for now.
  `description` is **required** by `PluginManifest`, and `targetAbi` is `10.0`, not `10.1`
- `src/…/JobNames.cs` — the four names and their default crons, as constants
- `src/…/TorrentDownloaderPlugin.cs` — `IPlugin`, `IScheduledTaskPlugin`, `IUiPlugin`; four jobs with
  the defaults from `docs/04-domain.md` § Settings
- `src/…/Views/Pages.cs`, `tests/…Tests/ManifestTests.cs`, `tests/…Tests/TorrentDownloaderPluginTests.cs`,
  `TestSupport/FakePluginContext.cs`, `TestSupport/CapturingLogger.cs`

**Steps**
1. Test: the manifest deserialises with `PluginManifest`, and its version equals
   `PluginIdentity.Version`.
2. Test: the manifest's id is exactly `1SBQT26FHF98EBRPYVRGD92CZF`.
3. Test: `NavEntries` and the manifest's UI mounts agree, entry for entry.
4. Implement. `Initialize` stores the context and does no I/O.
5. Test: `ExecuteAsync` throws `ArgumentOutOfRangeException` on an unknown job name.
6. Test: four ticks produce one "awake" line, naming the version.
7. `Dispose` cancels the lifetime token and is safe twice.

## S0-03 · The activity journal

**Read first:** `docs/03-architecture.md` § Observability.

**Files:** `Core/Activity/ActivityStage.cs` (`Harvest`, `Names`, `Find`, `Decide`, `Grab`,
`Download`, `Dispatch`), `ActivityEvent.cs` (which also holds `ActivityOutcome`:
`Started`, `Finished`, `Failed`), `IActivityJournal.cs`, `ActivityJournal.cs`, `ActivitySnapshot.cs`

**Steps**
1. Test: 1000 events from `Parallel.For` produce a snapshot with no torn state.
2. Test: a subject that starts and finishes is not in the in-flight list.
3. Test: history is bounded — 600 events leave 500, and the 500 kept are the newest.
4. Test: a snapshot already handed out does not change when the journal does.
5. Test: a failure clears the in-flight entry too, and carries its reason.
6. Test: every stage of the chain is in `ActivityStage`, in the chain's order.
7. Implement.

## S0-04 · Pushing the snapshot, and the dashboard

**Read first:** `docs/08-ui.md`.

**Files:** `src/…/Hosting/LiveSnapshot.cs`, `src/…/Views/DashboardView.cs`,
`Core/Activity/CycleStatus.cs` — the cadence timing the journal cannot answer,
`tests/…Tests/Views/DashboardViewTests.cs`, `tests/…Tests/Hosting/LiveSnapshotTests.cs`,
`TestSupport/FakeHub.cs`, `TestSupport/Rendered.cs`

**Steps**
1. Test: an idle snapshot renders a status bar saying when the last cycle ran and when the next is
   due — not a spinner, not an empty state.
2. Test: never having run says so, and is never drawn as nought.
3. Test: two in-flight episodes render two rows in **Now** with their stage and what each waits on.
4. Test: ten journal changes in 100 ms produce one push.
5. Test: after the burst it goes quiet again — the interval is a floor, not a heartbeat.
6. Test: a push that throws does not propagate, and does not stop the next one.
7. Implement, and serve the dashboard at `/`: a page nobody can reach is not a page.

**Done when:** the dashboard is a pure function of the snapshot, with no store access of its own.

## S0-05 · Settings

**Read first:** `docs/08-ui.md` § Settings, `docs/04-domain.md` § Settings.

**Files:** `Core/Domain/Profile.cs`, `Cadences.cs`, `ClientLimits.cs`, `Cron.cs` — five-field
validation, written here because `Core` references nothing,
`src/…/Configuration/Settings.cs`, `SettingsStore.cs`, `src/…/Views/SettingsView.cs`,
`src/…/Controllers/SettingsController.cs`, `TestSupport/FakeConfiguration.cs`,
`TestSupport/FakeSecretStore.cs`

**Steps**
1. Test: every setting in `docs/04-domain.md` § Settings round-trips with its documented default.
2. Test: an invalid cron is refused with the reason and the stored value is unchanged.
3. Test: an unwritable folder is refused with the reason.
4. Test: incomplete and intake on different volumes saves, with a warning on the page.
5. Test: a stored passkey and a stored API key render as *set*, never as their value — asserted the
   whole way through, from a real secret in the store to every prop of the rendered page.
6. Test: checking a folder leaves nothing in it. Finding out whether one can be written means
   writing to it, and only video files may live in a library folder.
7. Implement, including Run, Stop and the dry-run switch, which at this point do nothing and say so.

**Note:** `SettingsStore` probes a folder by writing to it, because existence is not permission. The
case that probe catches — a read-only share, a full disk — has **no automated test**: there is no
in-process way to make a folder that can be created and cannot be written. The test that exists
covers the folder that cannot be created at all.

---

# Sprint 1 — Library and the missing list

## S1-01 · Reading the library

**Read first:** `docs/02-library.md`.

**Files:** `Core/Domain/Show.cs`, `Episode.cs`, `EpisodeKey.cs`, `LibraryKind.cs`,
`Core/Ports/ILibrary.cs`, `src/…/Hosting/HostLibrary.cs`,
`tests/…Tests/TestSupport/FakeLibraryQuery.cs` — a fake `IPluginLibraryQuery`, which is what the
adapter's tests need; it returns every show in every library for a null id, exactly as the real one
does, so the film test is an outcome rather than an inspection.

`tests/…Core.Tests/TestSupport/FakeLibrary.cs` — a fake `ILibrary` — is deferred to `S1-02`, which is
the first slice with a Core test that needs one. A test double with nothing using it only rots.

**Steps**
1. Test (**C6**): libraries are enumerated and only media types `tv` and `anime` are read; a
   `movie` library's shows never appear.
2. Test: `GetShowsAsync` is called **per library id**, never with null.
3. Test: a show whose `Folder` is null is skipped.
4. Test (**C7**): presence is derived from each episode's `HasFile`; a show with
   `HaveEpisodeCount = 0` and episodes on disk is not treated as empty.
5. Test: the show's media type travels with it, taken from its library, so anime is known without
   guessing and the show's `LibraryId` is kept for the dispatch.
6. Test: `Year` comes through.
7. Test: an unknown library type is out of scope, and the type is matched without case — the column
   is a plain indexed string with no enum behind it.
8. Test: the air date crosses as a date, and its absence stays an absence.
9. Implement the adapter; it maps and nothing else.

**Note:** neither episode count is on the domain `Show` at all, and a test asserts that. The surest
way for a number that lies never to be read is for it not to be there to read.

## S1-02 · The missing list

**Read first:** `docs/02-library.md`, `docs/04-domain.md` § Episode states and § Storage schema,
`docs/10-known-failures.md` § B1, B2, B5.

**Files:** `src/…/Storage/Database.cs`, `Migrations/001-initial.sql`, `EpisodeRepository.cs`,
`Core/Pipeline/MissingRefresh.cs`, `Core/Domain/EpisodeState.cs`, `TrackedEpisode.cs`,
`tests/…Core.Tests/TestSupport/FakeLibrary.cs` — deferred here from `S1-01`, which had nothing to
use it

**Steps**
1. Test: an episode with a null or future air date is `NotAired` and never searched.
2. Test: an aired episode with no file is `Missing`, however old — an episode from two years ago
   counts the same as last night's.
3. Test (**B5**): a show that has ended is included, not skipped.
4. Test (**B1**): an `Unavailable` episode returns to `Missing` on a refresh.
5. Test (**B2**): a failed grab does not increment attempts.
6. Test: attempts and last-searched survive a refresh for rows that still exist; a row for an episode
   the library no longer has is deleted.
7. Test: the migration runner is idempotent, and the whole documented schema is what it creates.
8. Test: an episode the library already has a file for is not tracked at all — presence is the
   absence of a row.
9. Implement against a real temp-file SQLite database.

**Note on B2.** What this slice can prove is that nothing but a recorded search moves `attempts`:
a refresh does not, and giving up does not. The failed *grab* itself is `S6-01`, and the test that a
failed grab leaves the count alone belongs there, against a grab that can fail.

**Note on how `Unavailable` ends.** The refresh writes the derived state every time, so an
unavailable episode is missing again on the next maintenance pass and gets another turn. `attempts`
survives, so the count keeps climbing across passes; whatever `S4-04` does with it must therefore
count an attempt *after* trying, or an episode over the limit would be marked unavailable again
without ever being searched and the refresh would achieve nothing.

## S1-03 · Anime numbering

**Read first:** `docs/02-library.md` § Anime numbering.

**Files:** `Core/Domain/AbsoluteNumbering.cs`, and `Core/Pipeline/MissingRefresh.cs` fills in
`TrackedEpisode.Absolute`.

**Steps**
1. Test: absolute of S02E13 after a 24-episode season one is 37.
2. Test: season 0 never counts, and is never numbered either.
3. Test: a season with a gap still numbers from the episodes that exist.
4. Test: an episode whose earlier siblings are absent still gets its own number — the number is
   `episode + Σ earlier seasons`, **never** its position in the list. The two agree only while the
   list is complete, and part company exactly when episodes are missing, which is the case this
   plugin exists for.
5. Test: the map is built from the episode list already fetched — no extra library call.
6. Test: a show in a `tv` library has no absolute number.
7. Test: an episode already on disk still counts towards the offset, or the show renumbers itself
   every time something downloads.
8. Implement.

## S1-04 · Shows and Queue pages

**Read first:** `docs/08-ui.md`, `docs/10-known-failures.md` § G3.

**Files:** `Core/Domain/ShowSummary.cs`, `Core/Pipeline/QueueOrder.cs`, `ShowSummaries.cs`,
`src/…/Views/ShowsView.cs`, `QueueView.cs`, and `Views/Pages.cs` gains the route table

**Steps**
1. Test: the Shows page's missing count equals the rows for that show, from a seeded store.
2. Test: the media type is rendered per show.
3. Test: the Queue page separates *looking* from *waiting to air* and never counts an unaired episode
   as missing.
4. Test: the order is the order they will be asked in — the page uses the search cadence's own rule,
   not a second one.
5. Test: an episode never searched says so rather than showing a date or a nought.
6. Test: an anime episode is named by both its numbers.
7. Implement.

**Notes.** Shows and Queue are **not** navigation mounts — `docs/08-ui.md` § Navigation fixes those
at two, and a test asserts it. They are declared in `IUiPlugin.Routes` instead, which is what lets
the server list them, give each the shell it wants, and refuse a link to a page that does not exist.

There are **three** lists on Queue, not two: *given up for now* is its own, or an unavailable episode
appears in no count on any page and nobody can see it has stopped moving — B1's failure wearing a
different hat.

**Last arrival** on the Shows page waits for `S6-04`. It comes from `history`, nothing writes that
table yet, and a column reading "not recorded" on every row is noise rather than information.

**Sprint 1 done when** the Shows page matches a hand-counted library —
`tests/…Tests/HandCountedLibraryTests.cs` runs the whole chain against a library counted by hand in
its own comments. Confirming it against the **real** library on `beast-unit` needs a deploy, and a
deploy needs the owner to stop the server.

---

# Sprint 2 — Sources and fetch

## S2-01 · The catalogue and the host gate

**Read first:** `docs/05-sources.md`, `docs/10-known-failures.md` § C1, C2, B3.

**Files:** `src/…/sources.json`, `Core/Sources/SourceDefinition.cs`, `SourceCatalogue.cs`,
`SourceRole.cs`, `HostGate.cs`, `src/…/Configuration/CatalogueLoader.cs`,
`src/…/Hosting/HostGrants.cs`, `tests/…Tests/TestSupport/FakeGrants.cs`,
`tests/…Tests/Configuration/AssemblyFolderTests.cs`

**Steps**
1. Test (**C1**): the catalogue is read from the assembly's own folder and yields more than ten
   sources; a missing file logs and falls back.
2. Test: every host in `sources.json` is declared in `plugin.json`, and nothing extra is — both
   directions.
3. Test (**C2**): every owner-configured source's hosts are requested at runtime, search addresses
   included; shipped hosts are not requested because they are in the manifest.
4. Test: an owner source with the same name as a shipped one replaces it; a name in
   `DisabledDefaultSources` drops it.
5. Test: `HostGate` never lets two requests to one host be closer than its interval; ten requests to
   ten hosts run concurrently.
6. Test (**B3**): a 429 widens that host's interval and success narrows it; a permission refusal does
   neither. Success halves rather than resets, and the widening has a ceiling.
7. Test: a catalogue that will not parse is the same failure as a missing one, and gets the same
   answer — nothing, at error level.
8. Implement.

**Notes.** `SourceDefinition` carries **two** gating flags. Gating is a property of an address:
PreDB answers its feed over plain HTTP and puts its search behind a challenge, and one flag for both
would either send every feed read through the browser or walk the search into a challenge each cycle.

The manifest declares the hosts of **disabled** sources too. A manifest cannot change at runtime, so
a source the owner switches on next month must not need a release to become reachable.

**C1 needs a load-context test.** In-process, `AppContext.BaseDirectory` *is* the assembly's folder,
so every ordinary test passes either way and none of them can tell the fix from the fault. They
differ exactly where it matters — an assembly loaded from somewhere other than the process that
loaded it, which is what a plugin is — so `AssemblyFolderTests` copies the assembly elsewhere and
loads it there. It is the only test that fails when the folder is read from the process.

Both `HostGate` tests that wait on a fake clock are **bounded**. The failure they guard is not a
wrong answer but no answer: one gate shared between hosts leaves nine requests waiting on a clock
nothing will advance, and an unbounded wait hangs the suite instead of failing it.

## S2-02 · Fetching

**Read first:** `docs/05-sources.md` § Fetching, `docs/10-known-failures.md` § G1.

**Files:** `Core/Sources/IFetch.cs`, `FetchFailure.cs`, `IChallengeSolver.cs` — the solver ports and
`Clearance`, from `docs/07-solver.md` § The port, so the fetch can name what it needs;
`src/…/Hosting/ChallengeAwareFetch.cs`, `CloudflareChallenge.cs`, `ClearanceStore.cs`,
`tests/…Tests/TestSupport/FakeHttp.cs`, `FakeSolver.cs`

**Steps**
1. Test (**G1**): a refusal names the address and blanks anything matching
   `api_?key|apikey|passkey|token|secret|rss_?key` — the name stays, only the value goes.
2. Test: a value that merely looks secret is left alone. A passkey is forty hex characters and so is
   an info hash; blanking by shape makes every address useless for working out what went wrong.
3. Test: a gated host never makes an HTTP attempt, and one with no browser says *that* is what is
   missing rather than blaming the site.
4. Test: a challenge is solved once; a second after a fresh solve gives up with a clear sentence.
5. Test: clearance is spent on refusal, not trusted until expiry, and is sent under the user agent it
   was issued to.
6. Test: a host with no grant is never asked and earns no backoff.
7. Implement. `LastBody` is exposed for the health tool and cleared by the caller.

**Notes.** `IInPagePost` from `docs/07-solver.md` § The port is **not** here: nothing needs it until
TorrentBay's signed POST in `S2-06`, and a port with no caller cannot be judged.

`CloudflareChallenge` reads the **response** — the `cf-mitigated` header first, because that header
exists so a client need not read the page. Body markers are consulted only after the status has
narrowed it down, and they are deliberately the least a challenge can be identified by: there is no
capture of a challenge page in `tests/fixtures/` yet, so pinning more would be guessing at markup.
Take one when `tools/Capture` exists and tighten them against it.

Tests that fetch one host **twice** cannot use a fake clock: the second request waits on a gap that
nothing will advance, and the suite hangs instead of failing. They use the real clock with a nought
interval; what the gate does with intervals is `HostGateTests`' business.

## S2-03 · The hidden stage, and Chrome

**Read first:** `docs/07-solver.md` § No window and § The browser, `docs/10-known-failures.md` § D3, C5.

**Files:** `src/…/Solver/IHiddenStage.cs`, `WindowsDesktopStage.cs`, `XvfbStage.cs`,
`HiddenStages.cs`, `BrowserInstall.cs`, `Browser.cs` — the lifecycle the steps below describe, which
is neither the install nor a stage, `tests/…Tests/TestSupport/RecordingStages.cs`

**Steps**
1. Test (**C5**): across two starts the downloader is called once, and the second start is a
   different `Browser` because a server restart is what the fault happened across.
2. Test: an install whose browser has since gone is not an install. A half-deleted folder is a real
   state, and answering "installed" for it fails later and further away.
3. Test (**D3**): `CanHideABrowser` is false on macOS, and a stage that cannot hide reports the reason
   and starts nothing.
4. Test: the stage is created **before** the browser process starts, asserted on a recording stage.
5. Test: a second `StartAsync` reuses the running browser, and one that has died is started again.
6. Test: two `StartAsync` calls arriving together still produce one browser.
7. Implement. Headless is not used.

**Notes.** `HiddenStages.HidingFor(isWindows, isLinux)` is separate from asking the operating system
which it is, so the **macOS** answer can be asserted on a machine that is not a Mac. A rule about a
platform nobody runs the tests on is otherwise a rule nobody ever checks — and this one is the
difference between skipping gated sources and opening a window on somebody's screen.

**The ordering in step 4 is structural, not conditional.** Only an `IHiddenStage` can launch a
browser, so "browser before stage" cannot be written; no single-line mutation breaks it. The test
documents the rule and would catch a redesign that separated the two.

Windows launches through `CreateProcess` because the desktop is chosen through
`STARTUPINFO.lpDesktop`, which `Process.Start` cannot reach. Neither real stage can be tested here —
both are proven only on the server.

## S2-04 · The solver

**Read first:** `docs/07-solver.md`, `docs/10-known-failures.md` § D1, D2.

**Files:** `Core/Sources/IChallengeSolver.cs` — which already holds `IPageSource`, `Clearance` and
now `IInPagePost`, one file rather than four because they are one contract and are read together;
`src/…/Solver/BrowserSolver.cs`, `IBrowserTab.cs`, `PuppeteerTabs.cs`,
`tests/…Tests/TestSupport/FakeTabs.cs`

**Steps**
1. Test (**D1**): a JSON body fetched through the solver is raw JSON; the fixture is Chrome's viewer
   markup, so a DOM-scraping reader fails this test.
2. Test (**D2**): a navigation during the poll throws `Execution Context was destroyed`; the poll
   catches it and continues.
3. Test: one reload, then a sentence naming the host.
4. Test: two requests to one host share one tab; two hosts get two.
5. Test: clearance is kept per host with its user agent, and spent on refusal.
6. Test: `PostAsync` returns null when no solver can, and the caller says "this site needs a browser".
7. Test: an HTML page comes back from the **document** — that is where the site's own scripts have
   finished putting it, and re-fetching would get the markup from before any of them ran.
8. Implement.

**Notes.** The driver is **PuppeteerSharp**, which is what `docs/01-plugin.md` § Deploying already
meant by "the browser driver's assemblies". It *connects* to the browser on the port `S2-03` started
it on and never launches one: a driver knows nothing about hidden desktops, so letting it start
Chrome would put a window on the owner's screen.

Every judgement is in `BrowserSolver` and is tested against a fake tab — how long to wait, when a
navigation is the page working rather than failing, one reload then a sentence, and whether the
document is the answer or a picture of it. `PuppeteerTab` only does as it is told, because nothing
below that seam can be tested without a browser.

Whether the body is being viewed is decided by `document.contentType`, not by the address: a site can
serve JSON from a path that looks like anything.

## S2-05 · Readers, part one

Generic, 1337x, EZTV, KickassTorrents, LimeTorrents.

**Files:** `Core/Sources/Query.cs`, `Readers/SourceRow.cs` — which also holds `Html`,
`Readers/ISourceReader.cs` — and the `Readers` registry, `Readers/GenericReader.cs`,
`Readers/SiteReaders.cs`, `tools/Capture/`

**Steps**
1. Capture a real page per site into `tests/fixtures/` with `tools/Capture`.
2. One test per reader: non-zero row count, first row's title and detail address asserted.
3. Test (**E4**): a search for a full release name, against the page the site really answers.
4. Test: EZTV's own tag is stripped from every title.
5. Test (**C4**): a reader name nothing answers to resolves to **nothing**, never to the generic
   reader. The whole-catalogue version belongs to `S2-06`, where the last four readers land —
   asserting it here would only assert that they have not been written yet.

**Notes.** Readers are regular expressions and `Core` keeps its no-dependency rule. `static readonly
Regex` throughout: `[GeneratedRegex]` was measured returning zero matches on TorrentBay where the
identical inline expression returned fifty, and zero rows is what a site with nothing looks like.

**E4 no longer reproduces.** 0.3.4 measured Kickass answering a full release name with that release's
own page; on 14 August 2026 the same search answers a one-row listing, and
`kickasstorrents-full-name.html` is that page. The reader keeps the no-rows-take-a-magnet fallback
because a site that did this once may do it again, but **no capture demonstrates it and no test
covers it**. `docs/05-sources.md` says so.

Neither the EZTV nor the Kickass listing carries a magnet today, so both take 1337x's route: the row
carries its own page and the magnet is on it. Following it is `S2-06`'s work.

The 1337x size is read from its own cell rather than from the row. No capture distinguishes the two —
the first size-shaped string in that row *is* the size — so that scoping is defensive and unproven.

## S2-06 · Readers, part two

TorrentBay, TorrentGalaxy, Torrentz2, TorrentDownloads, TorrentFunk.

**Files:** `Core/Sources/Readers/SiteReaders2.cs`

**Steps**
1. Fixtures for all five.
2. Test (**D4**): TorrentBay's reader finds rows on the real capture — `static readonly Regex`.
3. Test (**E6**): several bare hashes yield none, exactly one yields it.
4. Test (**E1, E2**): TorrentFunk — bare attributes parse, and the title keeps its group.
5. Test: Torrentz2's foreign-site prefix is cut, anchored on ` - `.
6. Test: TorrentDownloads skips the advert row by matching the numeric id.
7. Test (**C4**): every reader name in `sources.json` resolves to a non-generic reader, read from the
   file that ships rather than from a list written in the test.

**TorrentBay's signed POST is not here.** The magnet is fetched by the site's own script from an
endpoint the page never names: each row carries only a `data-id`, and the page a `csrf-token`. Two of
the four values are therefore visible and the endpoint is not — it is in an external script that has
to be read. The reader produces the row and exposes the id; getting a magnet from it is the grab's
work in `S6-01`, and step 3 of this slice moves there with it.

**E6's rule lives in `Html.OnlyHash`, not in the TorrentGalaxy reader.** The reader takes no hash at
all, so a mutation there changes nothing — the test that bites is the one on `OnlyHash` itself.

**TorrentFunk answers thirteen real rows** behind a block of advertising that names the search term
and links to a third host. Its own rows use bare attributes on the cells, which is what E1 is about;
the hrefs are quoted, so the tolerance is proven by the cell regex rather than by the anchor.

## S2-07 · JSON and XML sources, owner sources, and the health tool

**Files:** `Core/Sources/Readers/DataReaders.cs`, `tools/SourceHealth/`

**Steps**
1. Fixtures and readers for apibay, eztv-api, srrdb, nyaa, predb, scnsrc.
2. Test: srrDB honestly answering zero is not a failure.
3. Test: an owner-configured torznab source is built, asked, and its API key never appears in a log
   line or an error message.
4. Build `tools/SourceHealth` per `docs/05-sources.md` § The health tool.
5. Test (**G2**): the captured body is cleared between sources; a rate-limited source is retried once
   and reported distinctly.
6. Test: a page with more than two release-shaped names and zero rows read is reported as a broken
   reader.

**Notes.** **Torznab is the one reader with no capture behind it.** This plugin has no Torznab server
to ask and one cannot be conjured, so its test uses the published shape written out in the test —
the only place in the repository that does. Everything about it that *can* be measured is: the
address is built, the key is really sent, and it appears in no failure, message or log line.

**srrDB writes every dash in a release name as `&#45;`**, so a name matches nothing at all until
numeric entities are decoded — and a scene name is mostly dashes. `Html.Decode` does it now.

**apibay says "nothing found" as a row saying so**, not as an empty array. Taking it would put a
release called *No results returned* into the name pool and search every indexer for it.

**Nyaa answers nothing for anything that is not anime**, which is an answer. Both captures are kept:
one asked for an anime, one for a live-action show.

**Sprint 2 done when** `dotnet run --project tools/SourceHealth` reports every enabled source
answering — sixteen of the seventeen shipped, YTS being switched off in the catalogue and so never
asked. Done on 15 August 2026: sixteen of sixteen, nothing flagged.

---

# Sprint 3 — Names

## S3-01 · Parsing release names

**Read first:** `docs/04-domain.md` § Release names, `docs/10-known-failures.md` § H3.

**Steps**
1. Fixture: the captured pages themselves, read through their own readers — no derived file of
   names, which is a second copy that drifts from the first. The anime cases the field table names
   were not on any capture, so five more were taken: `nyaa-absolute`, `nyaa-version`,
   `nyaa-subsplease`, `nyaa-diacritic` and `torrentdownloads-greek`.
2. A test per row of the field table, including `264`/`265` without a prefix, `H.265` with its dot, a
   show called *Greek*, a diacritic, `v2`, `137` versus `1080`, a season pack, an anime batch.
3. `TitleMatcher.Matches` — begins with, not contains. *Lucky* versus *A Bloody Lucky Day*.
4. Implement.

## S3-02 · Harvest

**Read first:** `docs/03-architecture.md` § Stages, `docs/10-known-failures.md` § A2.

**Steps**
1. Test (**A2**): a feed with no search address is read whole and never asked a query.
2. Test: all feed sources are read concurrently — a fake clock proves wall time is the slowest, not
   the sum.
3. Test: the pool is keyed by normalised show+slot and deduped by title.
4. Test: one failing feed does not take the harvest down.
5. Test: every harvest step publishes to the journal.
6. Implement, writing the pool to `name_pool` so a restart mid-cycle does not re-harvest.

**Notes.** The feed cadence is **not** wired to the harvest here. Running it needs the fetch chain,
the browser and the host grants assembled in one place, which is `S4-04`; half a chain would report
failures the owner cannot act on.

## S3-03 · Resolving a name for an episode

**Steps**
1. Test: an episode answered by the pool costs **zero** requests.
2. Test: a miss asks the name databases once per (show, season) — two episodes of one season cost one
   query, asserted on a recording fetch.
3. Test: a show needing its year is asked under both forms and the answers are pooled.
4. Test: a show from an `anime` library is asked under both the seasonal and the absolute form.
5. Test: forty-two episodes across six seasons cost six queries.
6. Implement.

**Notes.** `docs/02-library.md` says a show is asked with its year "where a show's title is a common
word" and defines no test for one. The rule here is **one word**: the four shows in the real library
that need it are all single words, and adding the year to every show doubles every request.

The pool key is exact, so a release whose title carries more than the show's — `One Piece (Elbaf
arc)` — is filed under a key nothing looks up. It costs a request and never a wrong download, and
fixing it means looking the pool up by slot and matching titles with `TitleMatcher`. Recorded under
**Decisions** rather than done here.

---

# Sprint 4 — Find and decide

## S4-01 · The profile

**Read first:** `docs/04-domain.md` § The profile, `docs/10-known-failures.md` § A1, H1.

**Steps**
1. Test (**A1**): a release carrying no torrent is **not** judged on seeders.
2. Test: a copy below the minimum is refused, with the site and the count in the history line.
3. Test: every other rule, one test each, each proven to fail when the rule is deleted.
4. Test: `x265` is refused when the profile says h264; an untagged release is refused when the codec
   tag is required.
5. Implement `ReleaseFilter` and `ReleaseDecider`.

## S4-02 · Find

**Read first:** `docs/05-sources.md` § Merging.

**Steps**
1. Test: all indexers are asked concurrently for one name.
2. Test: the same hash from five sources becomes one release with the union of trackers and the
   highest seeder count.
3. Test (**C3**): a row with no magnet is followed to its own page, once, only for the chosen release.
4. Test (**B4**): between two acceptable copies the higher-priority indexer wins.
5. Test: one failing indexer does not take the search down.
6. Implement.

## S4-03 · Deciding

**Steps**
1. Test: a season pack is taken only when the season's gaps reach `SeasonPackThreshold` and the
   profile allows it, and it answers for every gap it covers.
2. Test: a blacklisted title or hash is never chosen.
3. Test: an episode settled by a pack earlier in the same cycle is not asked about again.
4. Test: a refused release is recorded with its reason, so the Skipped page can show it.
5. Implement.

## S4-04 · The pipeline end to end

There is no torrent client yet — Sprint 5 builds it — so this runs against `FakeTorrentEngine`.

**Steps**
1. Test: over a seeded library and fake sources, one decision per episode is reported with the
   release, the site, the seeder count, and the reason for any refusal.
2. Test: every stage appears in the journal, so the dashboard's **Now** fills.
3. Test: forty-two episodes across six seasons cost the request count S3-03 asserts, end to end.
4. Test: with `DryRun` on, nothing is handed to the engine and the dashboard shows what it would take.
5. Implement.

**Sprint 4 done when** the dashboard, on a real library, shows what it would take for every missing
episode.

---

# Sprint 5 — BitTorrent

The protocol, written here, in `src/…Bittorrent/`, tested against captured wire bytes.
`docs/06-torrent-client.md` is the spec; read it once at the start.

## S5-01 · Bencode

1. Test: decode integers, byte strings, lists and dictionaries from a real `.torrent`'s bytes.
2. Test: a name that is not valid UTF-8 survives as bytes.
3. Test: encoding a decoded dictionary reproduces the input byte for byte.
4. Test: the reader reports the byte range of the `info` dictionary.
5. Test: malformed input is refused with the offset, never an exception from deep inside.
6. Implement over `ReadOnlySpan<byte>`.

**Notes.** The fixture is `tests/fixtures/ubuntu-desktop.torrent` — Ubuntu's own published torrent,
real and freely distributable, with 484 KB of piece hashes in it. `tools/Capture` grew a `--file`
form to save it: a `.torrent` is binary, and the plugin's fetch answers a string. Every number
asserted about it was read out of the file by a second implementation, so no test here is this
parser agreeing with itself.

## S5-02 · Torrent metadata and magnets

1. Test: the info hash of a real `.torrent` matches the known hash, computed over the raw `info`
   bytes.
2. Test: single-file and multi-file torrents both yield the right file list and total size.
3. Test: a magnet parses hex and base32 `xt`, `dn`, and every `tr`.
4. Test: `info.private` is read.
5. Test: piece boundaries across a multi-file torrent map to the right file and offset.
6. Implement.

## S5-03 · The engine shell and its port

1. Test: the engine starts on `Initialize` and stops on `Dispose`, once each across four cadence
   ticks.
2. Test: `Dispose` twice is safe.
3. Test: `ListenPort` is bound for TCP and UDP; a port in use is reported with the number.
4. Test: `TorrentState` distinguishes `FetchingMetadata`, `Stalled` and `Paused` from `Downloading`.
5. Implement `ITorrentEngine` and `FakeTorrentEngine`.

## S5-04 · Trackers

1. Test: an HTTP announce sends every required parameter and parses compact peers from a captured
   response.
2. Test: a UDP announce does connect then announce; the connection id is reused within a minute and
   renewed after.
3. Test: UDP retry backoff is `15 * 2^n`, up to eight tries.
4. Test: all trackers announce in parallel; one failing does not stop the others.
5. Test: `started`, `completed` and `stopped` are sent at the right moments.
6. Implement.

## S5-05 · Peer wire

1. Test: the handshake bytes are exact, and the extension and DHT bits are set.
2. Test: a peer with the wrong info hash is dropped.
3. Test: every message type round-trips against captured bytes.
4. Test: a block nobody requested drops the peer.
5. Test: messages split across TCP reads are reassembled.
6. Implement.

## S5-06 · Pieces, verification and disk

1. Test: rarest-first picks the piece the fewest peers have.
2. Test: the first four pieces are picked at random.
3. Test: endgame requests the outstanding blocks from every unchoked peer and cancels the losers.
4. Test: a piece failing SHA-1 is discarded and its contributors penalised; two failures ban a peer.
5. Test: a piece straddling a file boundary is written to both files at the right offsets.
6. Test: files are created sparse at full size.
7. Implement.

## S5-07 · Metadata from peers

1. Test: the extension handshake advertises `ut_metadata` and reads the peer's message id.
2. Test: metadata pieces are requested, assembled and verified against the info hash.
3. Test: a peer whose metadata does not hash correctly is dropped.
4. Test: metadata not arriving within `MetadataTimeoutMinutes` fails the torrent with the reason, once
   and not once a tick, and a paused torrent is not failed by the clock.
5. Implement.

**Blacklisting the hash and returning the episode to missing is not this slice's.** Both need the
grab — the only thing that knows which episodes a hash was fetched for — and that arrives in `S6-01`,
which has the step. This slice fails the torrent and says why; `S6-01` acts on it, for a metadata
timeout and for a stall (`S5-12`) alike.

## S5-08 · Encryption

1. Test: the MSE handshake — DH key agreement, the `req1`/`req2`/`req3` hashes, and the RC4 keystream
   discard of 1024 bytes. **A captured exchange was not possible**: no peer in a real swarm will
   accept a connection from this machine (see `PROGRESS.md` § Decisions, which has the measurements).
   The cipher is put to RFC 6229's published vectors instead, the prime to a primality test, and the
   exchange to this client's own other end — so the two constants that would make a client talk only
   to itself are checked against something outside this repository.
2. Test: both crypto methods are offered and plaintext is accepted when the peer chooses it.
3. Test: an outgoing connection tries encrypted first and falls back to plaintext.
4. Implement.

## S5-09 · DHT

1. Test: the routing table is Kademlia — buckets split, and the closest nodes come back in order.
2. Test: `ping`, `find_node`, `get_peers` and `announce_peer` encode and decode against captured
   packets.
3. Test: a `get_peers` walk converges on the closest nodes and collects peers.
4. Test: the table is persisted and reloaded, so a restart does not re-bootstrap from nothing.
5. Test: a private torrent never touches the DHT.
6. Implement.

## S5-10 · Peer exchange and local discovery

1. Test: `ut_pex` added and dropped peers are read and offered, at most once a minute per peer.
2. Test: LSD announces on the multicast group and reads another announce.
3. Test: a private torrent does neither.
4. Implement.

## S5-11 · Rate limits, choking and seeding

1. Test: a token bucket at 1 MB/s passes 1 MB in a second and no more, on a fake clock.
2. Test: global and per-torrent limits both apply, and the lower wins.
3. Test: changing a limit takes effect without a restart.
4. Test: choking unchokes the four best downloaders every ten seconds and one at random every thirty.
5. Test: seeding stops at `SeedRatio` or `SeedHours`, whichever comes first.
6. Test: a private torrent is never stopped early by that rule.
7. Test: a passkey never appears in any rendered string, log line or journal entry.
8. Implement.

## S5-12 · Resume, recovery, stalls, pause and ports

1. Test: resume is written on a clean stop and on the interval; a reload verifies nothing already
   verified.
2. Test: a file whose size or timestamp changed is re-verified.
3. Test: pause keeps the pieces and resume continues from them.
4. Test: a hash in the store but not the engine is re-added from its magnet.
5. Test: a hash in the engine but not the store is stopped, files kept, logged.

   **Steps 4, 5 and 6 decide and do not act.** `Recovery.Plan` sorts every torrent into add, stop,
   stage or carry, and is tested against all four; carrying the plan out needs the store of grabs,
   which `S6-01` writes. `S6-01` has the step.
6. Test (**F4**): a torrent that finished while the server was down is staged on the first transfers
   tick.
7. Test: no progress and no peers for `StallMinutes` is stalled; progress without peers is not, and
   peers without progress for a minute is not.
8. Test: UPnP is tried, NAT-PMP is the fallback, and a failure is reported on the Settings page while
   the client carries on.
9. Implement.

## S5-13 · The client, joined up

**Read first:** `docs/06-torrent-client.md` § Lifecycle and § Around the transfer.

Sprints 5's slices each built a part — trackers, peers, pieces, disk, metadata, encryption, DHT, peer
exchange, rate limits, resume — and **nothing drives them**. `BittorrentEngine.AddAsync` records a
torrent and answers its hash; no socket is opened for it, so no magnet has ever downloaded a byte.
That is a fault in the plan rather than in the code: no slice was ever written for the loop that
joins the parts, and Sprint 5 cannot be accepted without it.

1. Test: adding a magnet announces to its trackers and dials the peers they name.
2. Test: a peer that completes the handshake is asked for metadata, and the torrent moves from
   `FetchingMetadata` to `Downloading` when it arrives.
3. Test: blocks are requested from unchoked peers, verified pieces reach the disk, and the bitfield
   and the resume file follow.
4. Test: `StatusAsync` reports real progress, rates, peers and seeds while it runs.
5. Test: the whole of it against a peer on this machine — a second instance of this client seeding a
   fixture torrent, since no peer in a public swarm will accept a connection from here.
6. Implement.

**Sprint 5 done when** a real magnet downloads to completion, survives a restart mid-flight, and the
dashboard shows it the whole way. `S5-13` is what makes that possible; the twelve slices before it
are its parts.

`S5-13` transfers a whole torrent between two instances of this client over a real socket, which is
that criterion proved as far as it can be proved here. What is left of it needs the owner: a **real**
magnet needs a peer in a public swarm to accept a connection from this machine, and none will —
nothing on this network maps a port, so nobody can dial in. Deploying to `beast-unit`, where the
port may be reachable, is what settles the rest.

## S5-14 · The engine drives the session

**Read first:** `docs/06-torrent-client.md` § Lifecycle, and `TorrentSession` itself.

`S5-13` proved `TorrentSession` downloads a whole torrent from another instance of this client over a
real socket. What it did not do is join that session to `BittorrentEngine`, which is the class behind
`ITorrentEngine` and the only one the plugin ever calls. `AddAsync` parses the magnet, records the
hash and the trackers, and stops: nothing announces, nothing dials, no metadata is fetched, no disk
is opened and no byte is ever written. `StatusAsync` reports the bookkeeping, `FilesAsync` answers
with nothing at all, and `PauseAsync` and `ResumeAsync` move a field.

So the client cannot download, and every part built on top of it — the grab, the transfers cadence,
the Downloads page, the pause and cancel actions — is correct against a client that never finishes
anything. **This is the slice that makes the plugin work**, and no slice was ever written for it,
which is why `S5-13` reads as done.

1. **Done.** Test: adding a magnet announces to every tracker it carries and dials the peers they
   name, once each however many trackers name the same one, and a tracker that will not answer costs
   that tracker alone. `TorrentRun` in the Bittorrent assembly, with `IPeerDialler` as the seam the
   sockets sit behind.
2. **Done.** Test: a peer that completes the handshake is asked for the metadata under the id that
   peer chose, the whole is checked against the info hash before any of it is believed, and the
   torrent comes out with its file list and the magnet's trackers. A peer that offers no id to ask
   under is asked for nothing at all.
3. **Done.** Test: the disk is opened under the download folder as soon as anything knows what the
   torrent is, and a restart starts from what the resume file says was verified. Verifying a piece
   before it reaches the disk is `S5-06`'s and is proved there.
4. **Done.** The run reports rates measured between the last two readings, never averaged over the
   transfer, and a reading taken too soon after the last keeps the one before it rather than dividing
   by nought. `BittorrentEngine` answers `StatusAsync` and `FilesAsync` from a `TorrentRun` that is
   really announcing, and a magnet taken on is announced to its trackers.
5. **Done.** `PauseAsync` stops the peers and keeps the verified pieces and the disk; `ResumeAsync`
   picks up from them, and a paused run announces to nobody. The rule lives with the run, so the
   engine no longer carries two methods that existed only for a test to reach.
6. **Done.** A real `ITrackerTransport` and a real `IPeerDialler` over sockets, both judged over the
   loopback: a datagram goes out and the answer comes back, a tracker that takes it and says nothing
   times out naming itself, one that is not there says so at once rather than waiting out the
   patience, and a peer that is not there is a peer that will not talk rather than an exception.
   The dial is encrypted first and in the clear when the peer will not have it.
7. **Done.** An accept loop on the listening socket. Plaintext and encrypted arrive on the same port
   and are told apart from the first byte, a peer asking for a torrent this client is not holding is
   dropped, and each arrival is welcomed on its own so a peer that dials and then says nothing does
   not hold the door. Proved over the loopback, dialled by this client's own dialler.
8. **Done.** The whole of it through `ITorrentEngine`: one engine seeds a fixture torrent from
   finished files and a resume that says so, another is given the same torrent, an empty folder and a
   tracker that names the first, and it announces, dials, handshakes, asks and writes on its own. The
   bytes on disk are the bytes that were seeded. `AddAsync` takes a `.torrent` as well as a magnet
   now, which `docs/08-ui.md` requires for `AddTorrent` and which is what lets one instance seed to
   another.
9. **Done.** `BittorrentEngine` holds one `TorrentRun` per torrent rather than a record, announces
   for each at the interval its trackers asked for, and the plugin gives it the real sockets. What
   remains of this slice is the accept loop and the acceptance run.

---

# Sprint 6 — Grab, staging, dispatch

## S6-01 · The grab

1. Test: a grab records the release, hash, source and every episode it covers.
2. Test: a grab the engine refuses is recorded as failed in the engine's own words and does not burn a
   search attempt (**B2**).
3. Test: the merged trackers and `DefaultTrackers` both travel with the request.
4. Test: free space is checked first, and a refusal names how much was needed and how much there is.
5. Test: a torrent the engine has failed — a metadata timeout (`S5-07`), a stall (`S5-12`) — blacklists
   its hash with the engine's own reason and returns every episode that grab covered to missing.
6. Implement.

## S6-02 · Completion and staging

1. Test: only video files are moved.
2. Test: the largest video in a multi-file torrent is the episode.
3. Test: a season pack yields one staged file per episode it covers.
4. Test: staging into an unwritable folder fails loudly and leaves the download alone.
5. Test: staging across volumes works.
6. Implement.

## S6-03 · Encode dispatch

**Read first:** `docs/09-host-contract.md` § Dispatching an encode — every line.

1. Test, with a fake service provider: the four type names are asked for, inside a scope, and
   `GetLibraryByIdAsync` is used rather than the Lite variant.
2. Test: when the file list matches nothing, **nothing is dispatched** and a warning names the file.
3. Test: `Id` is the server's match id as a string; `InputFile` is a full path; `SourceDriverId` is
   unset.
4. Test: the library is the show's own `LibraryId`, so an anime episode is dispatched to the anime
   library and a television episode to the tv library.
5. Test: the first `FolderLibraries` entry is used, with no preference.
6. Test: nothing throws — a missing type logs and returns false.
7. Implement, and record `dispatched` in history.

## S6-04 · Downloads page and history

1. Test (**G4**): a grab with no transfer yet renders a row saying so.
2. Test: progress, rate, peers, seeds, ratio and destination render from a seeded store.
3. Test: history shows grabbed, skipped with the reason, failed with the reason, dispatched with the
   library.
4. Implement.

**Sprint 6 done when** one real missing episode is downloaded on the server and an encode job appears
in the queue.

---

# Sprint 7 — Anime

## S7-01 · Anime naming

**Most of this was done in `S3-01` and the plan never noticed.** That slice parsed every name on
every captured page, and when the field table's anime cases turned out to be on none of the old
captures it took five more — absolute numbering, `v2`, a batch, a diacritic and a show called
*Greek*. So the grammar, the fixtures and their tests already existed by Sprint 3. What was left is
below.

1. **Done in `S3-01`.** Five Nyaa captures with real fansub names.
2. **Done in `S3-01`.** `v2` superseding `v1`, `[Group]` from the leading bracket, a title with a
   dash in it (*Mairimashita! Iruma-kun*), `EP1173` with no separator, and absolute numbering.
3. **Done here.** `01 ~ 12` was tested; the other word a batch is written with, `Complete`, was
   implemented and had no test at all. It has one now, over the two rows that really carry it.
4. Nothing to implement: the parser already read all of it.

**One rule has no capture to test it against**, and it is written down rather than faked: the pack
word only counts when nothing says which single episode it is, or a programme called *The Complete
History of Anything* would be a pack of a season it never mentions. No captured row anywhere carries
a single episode **and** a pack word — every one that has both has a range, which is a pack for its
own reason. Deleting the guard breaks no test. The next capture run should look for such a row.

## S7-02 · Dual-form search

1. **Done in `S3-03`.** The pool is asked under the seasonal key and the absolute key, and the name
   databases are asked under the bare title as well — an absolute-numbered release carries no season
   tag at all, so `Show S01` finds none of them.
2. **Done here.** A source may name the libraries it is worth asking about; one that names none is
   asked about all of them, so the field switches nothing off by omission. Nyaa names `anime`, and a
   television search does not ask it. "Ranked first" is the priority column: it is 50, above every
   general indexer, and `docs/05-sources.md` now says why.
3. **Done in `S4-01`.** `ReleaseFilter` matches an absolute-numbered release against the episode's
   own absolute, a batch range included.
4. Nothing else to implement.

## S7-03 · Anime end to end

1. **Done.** A seeded anime library, through the derivation and the whole chain, to a decision for
   the one missing episode — against the captured Nyaa page. It is the join `S1-03` and `S3-03` never
   had: one builds the absolute number from the library's own episode list and the other asks the
   pool under it, and nothing said whether the number the library produces is the number a release is
   really posted under. An absolute out by one fails the test, which is what it is for: a fansub row
   carries no season tag at all, so a wrong number finds nothing while every page still reads as
   though the plugin were working.
2. **Done in `S1-04`.** The Shows page has a Type column and a test that reads *anime* off it.
3. **Waiting on the owner.** A real dry run over the real anime library needs 0.4.0 on `beast-unit`,
   and a deploy needs the server stopped. It is in `PROGRESS.md` § Blocked with the other acceptance
   runs.

---

# Sprint 8 — Finish

## S8-01 · The remaining pages

Downloads, History, Skipped, Sources per `docs/08-ui.md`, each with a test that renders from a seeded
store and asserts the numbers (**G3**, **G4**). Downloads and History are done in `S6-04`; Skipped and
Sources are what is left.

Also: **the Settings page says when the port could not be mapped, and what to do about it.** UPnP is
tried, then NAT-PMP, and when both fail the page tells the owner in plain words that TCP and UDP
`ListenPort` need forwarding to this machine by hand — with every refusal the router gave. The
owner asked for exactly this on 18 August 2026, and it is what makes an unreachable client something
they can fix rather than something they have to notice. `PortMapping` already collects the reasons;
nothing renders them yet.

## S8-02 · The remaining actions

**Read first:** `docs/08-ui.md` § Actions and § Detail pages.

The steps below were written as though the actions were all that was left, and they are not. Every
one of them is a control on a page the route table does not serve, reaching a torrent client the
plugin never constructs, against a grab store nothing has ever written a row into. Sprint 6 built
its parts and no slice ever wired them, so this slice is three things in order, each green and
pushed on its own.

**Part one — the pages are reachable.**

1. Test: the route table serves Downloads, History, Skipped and Sources, and every one of them
   renders from a seeded store.
2. Test: `source_reports` is written by a real ask and read by the Sources page, so the page states
   what each site last did rather than what it was configured to do.
3. Implement.

**Part two — the transfers cadence.**

4. Test: a grab records a row, and the transfers tick moves it along.
5. Test (**F4**): a torrent that finished while the server was down is staged on the first tick, and
   an encode is dispatched for it.
6. Test: a torrent the client has failed blacklists its hash and returns its episodes to missing.
7. Implement, and make the engine something the plugin owns and starts once.

**Part three — the actions.**

8. **Done.** `RunNow` starts a cycle on the plugin's own lifetime and answers at once — proved with a
   request token cancelled before the request was made (**F1**) — and two at once are one.
   `StopRun` cancels the running cycle and says so when there is none.
9. **Done.** `SearchNow` looks for one episode immediately, on the plugin's own lifetime, and refuses
   an episode this plugin is not tracking rather than claiming to search for one it has never heard
   of. The narrowing happens before the cycle, so a pack still settles what it settles.
10. **Done.** `PauseDownload` and `ResumeDownload` reach the engine and move the grab's state, and a
    hash this client is not holding is refused by name rather than answered "ok".
11. **Done.** `CancelDownload` stops it, deletes its files, forgets the grab and returns every
    episode it answered for to missing — and does **not** blacklist it: the owner said no to this
    download, not to this release for ever.
12. **Done.** `AddTorrent` takes a magnet or a `.torrent`, writes it down like any other grab so the
    transfers cadence stages it, and refuses anything else with the reason and the source named.
13. **Done.** `AllowRelease` records an `allowed` history entry naming what it had been refused for,
    and clears the episode's last-search time so the next cycle looks again. Allowing something
    nothing ever refused is refused itself.
14. **Done.** Every action is a control as well as an endpoint, and a test says so: the Queue's rows
    search, the Skipped page's rows allow, Downloads carries a pause-or-resume button and a
    confirmed Cancel per download and a form to add one by hand. Where each sits, and why, is now in
    `docs/08-ui.md`, which had said only that a control must exist.

## S8-03 · Health automation

1. **Done.** `health/baseline.json` records what each source answered with, and a source answering
   with fewer rows than last time is flagged though it answered. Only what really answered is written
   down: a broken reader's nought would set the bar at nought and the rule would never fire again.
2. **Done.** It exits non-zero when anything is flagged.
3. **Done.** The README says to hand the report and the page over together, why the page has a shelf
   life of about a day, and how to read a source flagged once against one flagged every run.

## S8-04 · Hardening

**Step 0, and it did not exist when this slice was written: the first cadence tick after a restart
deadlocked.** Found by writing step 5 and fixed; it is under **Decisions** in `PROGRESS.md`. The
regression test is `TheFirstCadenceTickAfterARestartFinishes`, and it races the tick against a clock
rather than awaiting it.

1. **Done** in `S8-02` (**F1, F2**): a run started with an already-cancelled token still runs and the
   endpoint answers at once.
2. **Done** (**F3**): `OneAtATime` is the guard, with tests for one at a time, for opening again
   afterwards, and for exactly one of many arriving together getting in.
3. **Done.** Stop cancels the cycle and leaves the transfers alone.
4. **Done.** A restart mid-cycle does not grab again what it has grabbed, and does not re-harvest
   what is already in the pool.
5. **Done.** A cycle stopped on purpose is not reported as a fault, and one that cannot even prepare
   says so rather than vanishing — it is started from a button and nobody awaits it, so an exception
   there went nowhere at all.
6. **Done.** `scripts/deploy-to-server.ps1` refuses while the server is running, still sends base64
   over ssh, verifies every file's hash, and now ships `sources.json` — which it never had. The file
   list is checked against the projects the solution really builds.

## S8-05 · Ship it: a README, one version, and a deploy that works

Called "Release 0.4.0" until 25 August 2026, which was wrong twice over. It released nothing — step 4
is still waiting — and 0.4.0 is not a number this plugin had earned. What it really did is above.

The `v0.4.0` tag step 5 left behind is on `ecc0241` of 21 August. **S10-08 deletes it**: it is 74
commits behind, it predates every fix of the week that followed, and it stands in the way of the real
0.4.0, which is **S10-09**.

1. **Done.** The README says what it does, how to build, how to deploy — including that the script
   now refuses while the server is running, and that it ships `sources.json` — and how to read the
   health report, which `S8-03` wrote.
2. **Done.** Three things carry the version: the manifest, `PluginIdentity`, and the compiled file.
   The third was the compiler's default of 1.0.0 for the whole of 0.4.0's development. It is set once
   in `Directory.Build.props` now, and a test holds all three together.
3. **Done.** The first install on 21 August 2026 went
   over with every hash matching and the plugin still could not load: a class library does not copy
   its packages into its output, so twelve assemblies were named and three were there. Three faults
   in all, none of which could show on a server that already had the plugin — the remote path built
   here out of an unexpanded `$LOCALAPPDATA`, the plugin folder never created, and the dependencies
   never built. All three are fixed and the script no longer keeps a list of filenames, and it has
   deployed cleanly on every stop since.
4. **Done, on 25 August 2026.** Sugar S02E04 was found, downloaded, staged, dispatched with its own
   episode id and encoded into the library at 22:33, with the log and the dashboard as evidence. That
   is **S9-04**, which is where it is written down.
5. **Done.** The owner asked on 20 August 2026 and `v0.4.0` is tagged and pushed. It names a commit
   that had not then run on a server; if the owner would rather it named the one that passes step 4,
   it moves.

# Sprint 9 · Finishing it

**The one thing that decides this sprint:** an episode the library is missing goes all the way to
being an episode the library has, without the owner doing anything but installing the plugin.

Everything up to staging is proved on the owner's own server. The step it exists for — asking the
encoder, and knowing what became of that — has never once succeeded there. Until it does, nothing
else is worth releasing.

## S9-01 · Build against the contract that ships — **done**

**Read first:** `Directory.Build.props`, `docs/09-host-contract.md`.

The plugin builds against `0.1.404`. That is the **dev** version and it never moves; the released
server is on **0.1.478**. So every contract added since — `PluginTableCellType.Actions` and
`PluginTableAction` among them — is invisible here.

1. Correct the fact in `PROGRESS.md`: the contract is packed from the **released** server, not from
   `dev`. It has said `dev` since S0 and that is why this was missed.
2. Move `NoMercyContractVersion` to the released version, clear the NuGet cache, build.
3. Refresh `docs/reference/plugin-abi-*.txt` from the package that is really referenced, so the next
   reader sees what exists rather than what existed in July.
4. Anything the compiler now objects to is a contract that moved. Fix it here, and write what moved
   into `docs/09-host-contract.md`.

**Done when** the build is clean against the released contract and the ABI dump matches it.

## S9-02 · The buttons live in the table — **done**

**Read first:** `src/.../Views/DownloadsView.cs`, the new `PluginTableAction` in the contract.

The Downloads page draws every row twice — once as a table, once as a strip of Pause and Cancel
buttons underneath — because a row could carry one action and no more. The contract now has an
`Actions` cell, and the web client renders it.

1. One column after **Destination**, of cell type `Actions`, carrying Pause or Resume and a
   destructive Cancel.
2. Delete `Ui.List("downloads-controls", …)` and everything that fed it.
3. The page test asserts one listing and both buttons in the row.

**Done when** the page has a single table and the two buttons sit in it.

## S9-03 · Every show in a library is in scope — **done, and reverted the same day**

**Do not do this slice.** It was carried out on 24 August 2026 and undone the same afternoon. It is
kept, rather than deleted, because a reader who worked out the same idea from first principles would
otherwise do it again — and it costs the owner their disk.

**What it said.** A show is skipped unless at least one of its episodes already has a file:

```csharp
if (!episodes.Any(episode => episode.HasFile)) continue;
```

and the owner's rule is simpler: a show in a television or anime library is in scope, whatever it has
on disk. A newly added show has nothing on disk, which is exactly when the plugin is most use, and it
was the one case that did nothing at all.

**What happened.** Within the hour the plugin was on **479 grabs**, and **456 of them were Family
Guy** — a show the owner has never watched. The reasoning was sound and the premise was false: a
library row is not a show the owner added. The server keeps rows for shows nobody asked for, in the
same table, against the same library id, with a folder and a full episode list. Nothing in such a row
tells it apart from a show they added. Having a file is the only thing that does.

`MaxSearchAttempts` does not save this. It bounds how long each episode is looked for; it does not
stop 456 of them being looked for at all.

**What replaces it, and when.** media-server **#36** stops identification importing shows on a guess,
so a library row means the owner asked for it; **#34** makes a newly added show visible. When both
land, library membership becomes the discriminator and this slice is right — the newly added show it
was written for is in scope on the day it is added. Neither is this repository's to close, and until
they do, the has-a-file rule stands.

**S10-01** is what makes that day one line: the rule is written once, in
`Core/Pipeline/Ownership.cs`, and both the refresh and the transfers tick ask it. Changing it in one
of the two places is how this went wrong.

**Done when** #36 and #34 have landed and `Ownership.Theirs` asks about membership. Until then this
slice is a warning, not work.

## S9-04 · Prove the encode end to end — **done**

Proved on the owner's server on 25 August 2026: Sugar S02E04 downloaded, staged, dispatched with its
own episode id and encoded into the library at 22:33 — the first episode this plugin has delivered
end to end.

**Read first:** `src/.../Hosting/Transfers.cs`, `src/.../Hosting/EncodeDispatch.cs`.

Everything here is written and none of it has ever run to the end on a real server. This slice is
not code first: it is evidence first, and code for whatever the evidence shows.

1. Deploy, let the cadence run on its own, and read three things: the grab reaching `dispatched`,
   the encoder taking the job, and the episode appearing in the library.
2. Whatever refuses it, the reason is already in the log by name — the folder, the file, the library,
   or the match. Fix that, with a test that fails first.
3. When the library has the episode, the staged copy and the download are deleted and the grab is
   `done`. Watch that happen once rather than trusting it.

**Done when** one episode has gone from missing to in the library with nothing done by hand, and the
folders it passed through are empty afterwards.

## S9-05 · What was left behind — **done**

Three episodes were staged before any of this bookkeeping existed. Their grabs said `done`, so
nothing would ever come back to them.

The plan was to ask the owner to import them by hand or delete them. That is not what this plugin is
for: **every tick reads the intake folder itself**, and anything no open grab is waiting on is
matched to the grab that put it there — by the release both carry — and asked for again. It then
joins the ordinary path: dispatched, then the library, then deleted.

A file no grab can be found for is left where it is and said once. It may be the owner's own, and
this plugin does not delete what it did not make.

**Done when** they are gone from the intake folder without anyone touching them. That is the deploy.

## S9-06 · Release 0.4.0 — **superseded by S10-09**

Not a slice any more, and not to be done. It is the same release as **S10-09**, written before the
audit and before it was known what 0.4.0 waits on.

What it asked for is in S10-09 and is more than it knew: 0.4.0 is not a date but the version where
this plugin stops reaching into the server by name, and it waits on five media-server issues, none of
them this repository's to close. What ships before then is **0.3.9**, which is **S10-08**.

# Sprint 10 — What the audit found

`docs/plan/AUDIT-0.3.9.md` is the report. Every slice here names the findings it closes.

**Nothing in this sprint changes what the plugin does.** Each one removes work, moves a rule to one
place, or puts a seam where the next change already lands. A slice that cannot be done without
changing behaviour is a slice that was wrong, and the plan gets fixed rather than the behaviour.

The gate for all of them is the same and it is stricter than usual: `dotnet test` must be green
**without a single test changing its expectation**. A test that has to be edited to accept the new
code is the proof that the behaviour moved. The only edits allowed are to a test's own construction
— a fake given one more constructor argument — never to what it asserts.

## S10-01 · One rule for whose show it is

Closes **A1**.

`MissingRefresh` decides which shows are searched for; `Transfers.NotOursAsync` decides which grabs
are cancelled for belonging to a show the owner does not have. They are one policy written twice,
and on 24 August 2026 changing one of them put the plugin on 479 grabs in an afternoon.

It is also the rule that changes when media-server #36 lands and library membership becomes the
discriminator. One place to change, not two.

1. A test that fails when the two disagree: give the fake library a show with no episode on disk,
   and assert that the refresh does not track it **and** that a grab for it is cancelled — one test,
   both sides.
2. `Core/Pipeline/Ownership.cs`: `public static bool Theirs(IReadOnlyList<Episode> episodes)`, with
   the comment that names the 479 grabs and says what replaces it when #36 lands.
3. `MissingRefresh` and `NotOursAsync` both call it. Neither holds the expression.

**Done when** deleting the body of `Ownership.Theirs` fails tests on both sides.
Read first: `docs/02-library.md`.

## S10-02 · One question, one answer, per tick

Closes **B1**, **B2**, **B3**.

The transfers cadence runs every minute. It asks the library for its shows once per staged file and
once per dispatch, asks for a show's episodes from two places with two caches, and reads the open
grabs twice.

None of it is wrong. All of it is the same question asked again inside one tick.

1. A test that counts: the fake library records how many times each method was called, and one tick
   over four staged episodes asks for the shows **once**.
2. `TickAsync` fetches the shows once and passes them to `StageAsync` and `DispatchAsync`.
3. One episode lookup per tick, shared by `NotOursAsync` and `FinishAsync`.
4. Staging returns what it wrote, so `LeftBehindAsync` does not re-read the open grabs to find out.

**Done when** the call counts in that test are what the tick actually needs, and every existing
test still passes untouched. Read first: nothing.

## S10-03 · A connection costs a connection

Closes **B4**, **B5**.

Every database call makes the data folder and sets `journal_mode`. The folder exists after the
first call and `journal_mode` is a property of the file, not of the connection: about 21,600
directory creations and 21,600 unnecessary round trips a day, for nothing.

`foreign_keys` is genuinely per-connection and stays exactly where it is.

1. A test that opens twice and asserts the second open issues no `journal_mode` statement.
2. `Store` makes the folder once and sets `journal_mode` once, on the first open of the process.
3. Settings are cached and the cache is dropped on save. A stale settings cache is worse than the
   round trip, so the invalidation gets its own test: save, then load, and see the new value.

**Done when** both tests fail if the caching is removed. Read first: nothing.

## S10-04 · Maintenance does maintenance

Closes **C1**.

The maintenance cadence runs at four in the morning and its whole body is a refresh that the search
cadence already does before each of its four daily cycles. The actual housekeeping is elsewhere:
history is pruned as a side effect of that refresh, and duplicate grab rows are cleared on the first
transfers tick behind a `_refreshed` flag.

Three pieces of periodic work, none of them in the cadence named for it.

1. Maintenance owns the housekeeping: prune the history, clear duplicate grab rows, and whatever
   else is periodic and not part of a search.
2. `RefreshAsync` refreshes and does nothing else.
3. The first-tick flag stops being about the transfers cadence. **Corrected on 25 August 2026 while
   doing this slice:** it read "the flag goes, and with it the special case that made a start
   different from a tick", and that would have thrown away a fix rather than moved it. The flag
   exists because a restart must settle within the minute instead of carrying whatever the last run
   left behind until the six-hourly cycle — the 24 August case, where a broken build had left shows
   the owner does not have. What is wrong is not that a start is special; it is that one tick of one
   cadence was. So the start settles once, whichever cadence ticks first, and no cadence has a first
   pass unlike its others.
4. Search keeps its own refresh: a cycle needs a fresh missing list and must not wait for four in
   the morning.

**Done when** no cadence has a first tick unlike its others, and the housekeeping is reached through
the maintenance cadence rather than through the first transfers tick. Read first: `docs/01-plugin.md`
§ The four cadences — **not** `docs/04-domain.md` § Cadences, which this slice named and which does
not exist.

## S10-05 · Nothing that nothing reaches

Closes **D1**.

`Ui.List`, `Ui.Container` and `Ui.EmptyState` have no caller.

1. Find every page that draws an empty state by hand and give it `Ui.EmptyState`. **Done on
   25 August 2026: there are none.** Every "nothing here" in the plugin is a table's own empty
   message through the one `Ui.Table` helper, and the two places that could have used an
   `EmptyState` carry a comment saying why they must not. The audit said otherwise and has been
   corrected.
2. `Ui.List` and `Ui.Container` go, unless step 1 finds a use for them. Step 1 found none, so
   `Ui.EmptyState` goes with them, and so do the three client names only they used — recorded in
   `docs/08-ui.md` § Components so nothing is lost.
3. A test that fails when a helper nothing draws is added to `Ui`, or this comes back.

The three unused members in the BitTorrent client — `Dht.BootstrapAsync`, `RequestLedger.Cancelled`,
`TorrentRun.ResumePoint` — are **not** touched. The client is proven and out of scope; they are
recorded in the audit so they are not lost.

**Done when** `Ui` holds only what a page draws and the pages look exactly as they do now.
Read first: `docs/08-ui.md`.

## S10-06 · A port for the encode

Closes **F1**.

Everything this plugin asks of the server sits behind an interface in `Core/Ports` — except the
encode, which is the concrete `EncodeDispatch` handed straight to `Transfers`. It is also the one
part that is already scheduled to change: media-server #30 gives plugins `IPluginEncoder` and #35
gives them the episode's id, and between them every line of reflection in `EncodeDispatch.cs` goes.

With a port that day is a new class beside the old one and one line of composition. Without it, it
is surgery on `Transfers` while it is the thing keeping the owner's library filling.

This is not a seam invented for testing. It is the pattern this codebase already uses in five other
places, missing from the one place a change is already dated.

1. `Core/Ports/IEncodeGateway.cs`, shaped by what `Transfers` actually needs: a staged file, the
   episode it is, the show it belongs to, where that show's episodes already are, and an answer.
   **Corrected on 25 August 2026 while doing this slice:** the answer is taken-or-not, not
   "queued or a reason". The caller acts the same way whatever the reason — leave the file staged,
   ask again next tick — and the refusal is already logged and journalled where it happens, so a
   reason handed back would be read by nobody. The interface requires the refusal to say why before
   it returns instead.
2. `EncodeDispatch` implements it. Nothing inside it moves.
3. `Transfers` takes the port.
4. The comment on the interface names #30 and #35 and says what the second implementation will be,
   so the next reader does not have to find this document.

**Done when** `Transfers` names no concrete host type and every test passes with only its
construction changed. Read first: `docs/09-host-contract.md`.

## S10-07 · The plan says what happened

Closes **E1**, **E2**.

**S9-03 "Every show in a library is in scope" is marked done and was reverted the same day.** A
reader following this plan would put the 479 grabs back. **S8-05 and S9-06 are both called "Release
0.4.0".**

1. S9-03 says it was reverted, why, and what replaces it — media-server #36, after which library
   membership becomes the rule and this slice can be done properly.
2. The release slices carry the numbers they really released.
3. `docs/02-library.md` matches the rule the code actually applies.

**Done when** every slice marked done describes code that exists. Read first: `docs/plan/PROGRESS.md`
§ Decisions.

## S10-08 · Release 0.3.9

Every slice above is done and the plugin does exactly what it did before, with less work behind it
and one place to change each rule.

`v0.4.0` is already a tag, on `ecc0241` of 21 August, which is 74 commits behind and predates every
fix of the last week — the upload policy, the video whitelist, the naming, the duplicate grabs, the
episode id. It was never published as a release; only `v0.1.0` ever was. It names a build nobody
should install and it stands in the way of the real 0.4.0.

1. Delete that tag, locally and on the remote.
2. `Directory.Build.props` and `plugin.json` say `0.3.9`. They say `0.4.0` today, which is a number
   this plugin has not earned.
3. `PROGRESS.md` says what 0.3.9 is: the chain closed, and the audit closed with it.
4. Tag `v0.3.9`.
5. Only when the owner asks.

**Done when** the owner says so. Read first: `docs/plan/AUDIT-0.3.9.md`.

## S10-09 · Release 0.4.0 — on the contract, with no reflection left

0.4.0 is not a date. It is the version where this plugin stops reaching into the server by name.

**All five media-server issues it waited on were closed on 30 August 2026**, and the contract that
carries them is `0.1.479`, which this repository builds against. Steps 1 to 4 are done. Step 5 is
the version and the tag, and step 6 says when: only when the owner asks.

| Issue | What it gives |
| --- | --- |
| #30 | `IPluginEncoder` — asking for an encode without `IJobDispatcher` or `VideoEncodeJob` |
| #35 | `PluginLibraryEpisode.Id` — naming the episode without `MediaContext` |
| #36 | identification stops importing shows on a guess |
| #34 | a newly added show is visible, so membership can be the rule |
| #37 | deleting a show stops leaving its subtree behind |

1. **Done.** `ContractEncodeGateway` uses `IPluginEncoder` and `PluginLibraryEpisode.Id`, and
   `EncodeGateway.For` chooses between it and the reflecting one per server. It cost a class and one
   line of composition, with no line of `Transfers` touched — which is what **S10-06** was for.
2. **Done.** `EncodeDispatch.cs` is deleted — 588 lines, and every server type this plugin ever
   named by hand with it. There is no reflection anywhere in the plugin. A server that does not
   offer `IPluginEncoder` is told so, once, in the log and the journal, rather than guessed at: it
   needs contract `0.1.479` or newer.
3. **Done.** `Ownership.Theirs` is library membership. Measured before it was changed: the owner's
   television library held fifty-five shows and not one without a file, so the rows that made the
   old rule necessary are gone. A show is in scope the day it is added.
4. **Done.** `docs/09-host-contract.md` describes what runs, and the ABI dump is taken from
   `0.1.479` — the dump before it was `0.1.478` and carried none of these contracts.
5. `Directory.Build.props` and `plugin.json` say `0.4.0`, and the tag names the commit that proved
   it on the owner's server.
6. Only when the owner asks.

**Done when** the plugin names no server type it does not get from
`NoMercy.Plugins.Abstractions` — and the owner says so. Read first: `docs/09-host-contract.md`.

# Sprint 11 — What the first watched run has to be run against

Five things stand between this build and the first end-to-end run watched on the owner's own
server. One is a policy the plugin breaks today; three are mess of this repository's own making; the
last is the run itself, and it is the owner's.

**Read before starting: the paragraph below. It corrects the plan this sprint was written from.**

## The correction this sprint is built on

The list this sprint comes from said: take the show id the metadata providers answer with, hand it
to the encoder as `mediaId`, **and the encode job will add the show**. It will not, and the server
says so in its own source.

`IPluginEncoder.EncodeAsync(file, libraryId, mediaId, presetId, ct)` is implemented by
`NoMercy.Data.Plugins.PluginEncoder`. It resolves the library, takes its first folder, and queues a
`VideoEncodeJob` whose `Id` is the `mediaId` verbatim. `VideoEncodeJob.GetFileMetaData` then reads
that id back exactly once, in one of two ways and no others:

```csharp
Movie?   movie   = ... context.Movies  .FirstOrDefaultAsync(x => x.Id == Id.ToInt())
Episode? episode = ... context.Episodes.FirstOrDefaultAsync(x => x.Id == Id.ToInt())

if (movie is null && episode is null)
    return new() { Success = false };
```

and every caller of it does `if (!fileMetadata.Success) return;`. Both tables are keyed by the
provider's own id with `DatabaseGeneratedOption.None`, so `Id` is a **movie id or an episode id** —
never a show id. Nothing on that path creates a `Tv` row, a season, an episode or a library entry.

Three things follow, and all three are load-bearing for this sprint.

- **A show id sent as `mediaId` adds nothing.** It matches no episode; against a movie library it
  could match an unrelated film, which is worse than doing nothing.
- **`mediaId: null` adds nothing either**, and never did. `PluginEncoder` writes
  `Id = mediaId ?? string.Empty`, `string.Empty.ToInt()` resolves no row, `Success` is false, and
  `Handle` returns having done no work — while the queue records the job as finished. That is,
  line for line, what the owner watched on 31 August 2026: nine files handed over, nine jobs
  reported finished within two minutes, nothing written to the library.
- **The only thing that adds a show is `ShowImportJob`**, dispatched by
  `InboxRoutingService.DispatchImportJob` after the server has moved the file into the library
  folder itself. That is the dashboard's *Add content*, and it is the server's to run.

So there is no id to pass and no job to pass it to. The policy stands unchanged and is the whole of
**S11-01**: the plugin looks a show up, says what it found, and adds nothing. Read against contract
`0.1.481`, which is what the owner runs; `IPluginEncoder`, `PluginEncoder` and `VideoEncodeJob` are
byte-identical to `0.1.479`.

## S11-01 · The plugin looks a show up. It never adds one.

`ShowAdmission` adds a show to a library. It searches the server's metadata providers, takes the
nearest candidate, and dispatches `ShowImportJob(tmdbId, libraryId)` by reflection — the same call
the dashboard makes when a person adds content. It is named for permission and it does not ask for
any: it decides, on a release name parsed out of a file, that a show belongs in the owner's library.

That is not this plugin's decision. **The plugin fills a library the owner has built. It does not
build it.**

What the lookup is still worth keeping for is the sentence it can write. "It holds episodes of
`Dark.Matter`, which is in no library" is parsed off a file name and may be a mangling of one;
"the providers know it as Dark Matter (2024)" is the name the owner will type into *Add content*.

1. `Hosting/ShowAdmission.cs` becomes `Hosting/ShowLookup.cs`. `AddAsync` becomes
   `FindAsync(string title, int? year, CancellationToken ct)` and answers a `FoundShow(string Title,
   int? Year, int ProviderId)` or null. `Dispatch`, `DispatcherType` and `ImportJobType` go with the
   old name; `SearchAsync` and the candidate-picking rule are untouched, because choosing the wrong
   show to *name* is the same fault as choosing the wrong one to add.
2. `Ready()` names one thing, the probe, and says what the plugin can and cannot do with it: it can
   say which show a torrent holds, and it cannot add it.
3. `Transfers.AdmittedAsync` goes. In its place the unplaceable report — which already exists, is
   already written once per run per torrent, and already goes to the Skipped page — carries the
   looked-up name where there is one.
4. `Transfers._admitted` goes with it. Nothing is asked of the server twice any more, and
   `_unplaceable` already holds the "say it once" rule for the report that replaces it.
5. **`IEncodeGateway.IdentifyAsync` goes, and `ContractEncodeGateway.IdentifyAsync` with it.** It
   exists to hand a file over with `mediaId: null` and let the server work it out; the correction
   above is the server's own source saying that does nothing at all, silently, and marks the grab
   `Done` on the way. A torrent nothing can be named for is reported and left where it is — which
   is what the branch below it already did, and the only branch that ever put files in the owner's
   library by guessing is gone.
6. `TransfersTests.APackForAShowTheServerDoesNotKnowIsHandedOverToBeIdentified` asserts the
   behaviour being deleted. It becomes `APackForAShowInNoLibraryIsNamedAndLeftWhereItIs`: nothing
   encoded, nothing staged, the download still on disk, the grab still open, and one line on the
   History page naming the show.

**Done when** no path in `src/` can cause a row to appear in the owner's library that the owner did
not ask for, and a pack for a show in no library says which show to add. Read first:
`docs/09-host-contract.md` § Dispatching an encode, and the correction above.

## S11-02 · The health tool counts a release once

`PageReleases.CountIn` answers "how many releases does this page appear to carry", read without the
reader, so that a reader seeing nothing can be told from a site having nothing. On the capture of
31 August 2026 it reported thirty releases on a TorrentBay page carrying fourteen and thirty-five on
a LimeTorrents page carrying seventeen. It no longer flags either as broken — that was fixed by
normalising the names — but the number in the report is still about twice the truth, and the report
is a page the owner reads.

Both over-counts have the same cause and it is not the normalisation. A name is grown outwards from
each marker it contains, up to `Reach` characters either side, and a name holding three markers —
`1080p`, `WEB-DL`, `H.264` — is grown three times from three different starting points. Where the
name is longer than the reach, the three growths stop in three different places and the set counts
three names. On top of that each row is written twice on the page in forms that survive
normalisation differently: TorrentBay's title link is `<b>South Park S15E12</b>.HDTV.XviD-FQM`, so
the run after the tag is `hdtv xvid fqm` while the `href` is the whole name with the torrent's id
on the end; LimeTorrents' `href` ends `-torrent.html`.

1. **Grow to the whole run.** `Reach` goes. A name is the maximal run of name characters around the
   marker, so every marker inside one name grows to the same span and the span is deduplicated by
   its position before it is ever read. The run is bounded by markup, quotes, slashes and commas —
   none of which is a name character — so this is still linear and still cannot backtrack, which is
   what `Reach` was there to guarantee.
2. **One spelling.** `Plain` keeps only ASCII letters and digits and makes every other character a
   separator, so `fqm[ettv]` and `fqm ettv` are one name. Tokens that are all digits go — a
   torrent's id on the end of an `href` is the difference between the link and the text of it — and
   so do the words a URL is made of and a release name is not: `html`, `torrent`, `magnet`,
   `download`, `php`.
3. **A name that is the tail of another name is that name.** `hdtv xvid fqm` is what is left of
   `south park s15e12 hdtv xvid fqm` when the run starts after a highlight tag. Only the tail, never
   the head: `south park s15e12 hdtv xvid fqm` is the head of `... fqm vtv avi` and those are two
   releases.
4. It under-counts and never over-counts, and the comment says so: two rows carrying the same
   release under different ids are one name, and one name is what it reports.
5. A test over `tests/fixtures/torrentbay.html` and `tests/fixtures/limetorrents.html` that runs the
   real check and asserts the count does not exceed the rows the reader read. Both fail on today's
   code. Not a test over every capture: a reader that caps its rows at a hundred reads fewer rows
   than the page carries, and rightly.

**Done when** neither captured page reports more releases than its reader reads. Read first:
`tools/SourceHealth/PageReleases.cs`.

## S11-03 · A doc block belongs to the member under it

Eighteen places in `src/` carry two or three `<summary>` blocks in one run of `///` lines. Every one
is a block that was pasted above a member it does not describe, and in most cases the member it does
describe is a few lines further down with no doc block at all — `Staging.Names` and
`Staging.Discover` are both documented above `Staging.Claims`.

Nothing here changes behaviour and nothing here is cosmetic either: a doc block over the wrong
member is a false statement in the place a reader looks first.

1. Each of the eighteen is read, and each stacked block either moves to the member it describes or
   goes, where the member below already says the same thing.
2. By hand. A script that moves doc blocks moves them to the wrong members.
3. The detector that found them runs clean afterwards: no run of `///` lines in `src/` holds more
   than one `<summary>`.

**Done when** no file in `src/` has two summaries in one doc comment, and no member that had a doc
block has lost it. Read first: nothing.

## S11-04 · The test that failed once

One test failed once, at the end of August, and every run since has been clean. There is no record
of which test it was and no captured output, so there is nothing to debug from — and a fix invented
for a failure nobody can name is a change that cannot be shown to fix anything.

What can be done is bounded, and it is worth doing:

1. Run the whole suite six times over and record it. Seventeen clean runs is not proof, and it is
   what there is.
2. Read the tests that are structurally able to fail once in a hundred runs — a real socket, a real
   clock, a real port — and judge each one. A test that can fail for a timing reason is a fault
   whether or not it is the fault, and `CLAUDE.md` § Testing already forbids it.
3. Fix what that reading finds, and write down what it did not find. **Invent nothing.** If no test
   is able to fail for a timing reason, that is the answer and it goes in `PROGRESS.md` § Facts.

**Done when** either a test able to fail on timing has been fixed, or it is written down that none
was found and over how many runs. Read first: `CLAUDE.md` § Testing.

## S11-05 · One run, watched — the owner's

This build has never been on the server. Nothing below is this repository's to do.

1. The owner stops the server; the build is deployed; the owner starts it.
2. Dark Matter is at `done` with 37 GB on disk. Put back to `grabbed` it gives a verification round
   rather than a fresh download, which is the cheapest way to watch the chain move.
3. Watch one episode go the whole way: looked up, dispatched, encoded, in the library — and only
   then cleared up.

**Done when** the owner has seen it. Read first: `docs/01-plugin.md` § Deploying.

## S11-06 · The show is added the way Add content adds it — corrects S11-01

**`S11-01` read the rule wrongly.** "The plugin may not add a show" is not "the plugin may not ask
for one to be added". The owner's rule is that the plugin does not build a library by hand; it
dispatches the server's own job with the right information and the server does the rest. That is
precisely what the dashboard's *Add content* is, and the plugin is allowed to make the same call.

So the encoder half of `S11-01` stands — a file handed over with no media id resolves no row and the
job finishes having written nothing — and the import half is put back.

1. `Core/Ports/IShowImport`: look a show up and ask for it to be imported, answering the title it
   asked for or null. A port so the cadence is held to an outcome — which show, which year, which
   library — instead of to reflection nothing can test.
2. `Hosting/ShowImport.cs` implements it: `IInboxMetadataProbe.SearchTvAsync` for the show, then
   `IJobDispatcher.DispatchJob<ShowImportJob>(id, libraryId)`. The only reflection in the plugin,
   because the contract offers no way to ask a provider anything or to queue one of the server's own
   jobs. `Ready()` names all three types at startup so a server missing one says so an hour before it
   matters rather than an hour after.
3. `Transfers` asks once per run per show. The import sits on the server's queue, so a tick a minute
   later still finds the show in no library; without this it dispatched the same import every minute
   for as long as the queue took.
4. Nothing else happens on that tick. The files stay where they are and the grab stays open, and the
   tick after the import lands takes it on like any other grab.
5. Where it cannot be done — no library of that kind, no provider that knows the show, a server
   without the parts — the pack is left alone and the History page names the show, once. The show is
   asked for again next tick, because nothing was dispatched and the answer can change.

**Done when** a pack for a show in no library ends with that show in the library and its episodes
dispatched by their own ids, with nobody pressing anything. Read first: `docs/09-host-contract.md`
§ Dispatching an encode.

## S11-07 · An encode the server filed under the wrong episode is still done

The Downloads page said `encoding` for South Park S15E12 while the server said no task was running.
Both were telling the truth about themselves, and neither described what had happened.

Read out of the owner's own `media.db`: the plugin dispatched with `153823`, the server's own id for
S15E12; the encoder logged `for 153823` and wrote
`/South.Park.(1997)/South.Park.S15E12/South.Park.S15E12.1%.NoMercy.m3u8`; and the post-encode
registration wrote its `VideoFiles` row against episode `153785` — season 0, "Chef Aid: Behind The
Menu" — twice. Episode `153823` has no file at all.

The plugin's only proof that an encode arrived is the library having the episode, so it waits. Six
hours later it gives the episode back to the missing list and the same gigabytes are fetched again,
for work that was finished the whole time and is sitting on disk under the right name.

1. `ILibrary.GetFilesAsync(showId)` — every file the library holds for one show, by path. The
   contract already answers it (`IPluginLibraryQuery.GetShowFilesAsync`), and it returns the misfiled
   row too, because that row still belongs to the show.
2. `Core/Pipeline/Landed.cs`: a file whose own name carries this season and this episode is this
   episode. The whole path, since the encoder names the folder for it as well. The two numbers are
   kept apart — `S12E15` is not `S15E12`.
3. `Transfers.FinishAsync` asks it **only where the server has said the job is finished** and the
   episode still shows no file. Read while a job is running, a file part-written would be taken for
   one that arrived and the download deleted underneath the encoder: that is the 36 GB fault, and the
   job status is what keeps this on the right side of it.
4. Said out loud when it is taken. The owner's dashboard shows the episode under a season it does not
   belong to, and nothing else on any page explains why.
5. A finished job that wrote nothing at all is still not done, and still not deleted.

**Done when** an encode the server finished and misfiled closes its grab instead of being waited out,
and a finished job that wrote nothing still is not. Read first: `docs/09-host-contract.md`
§ What became of the job.

## S11-08 · A pack keeps every file it staged

Watched on the owner's server, and it cost eight episodes.

Nine encodes were dispatched at 12:22:40 on 1 September 2026. Between 12:22:41 and 12:22:46 the sweep
of the intake folder deleted eight of the nine staged files — *"no grab of this plugin's is waiting on
it"* — one second after the encoder had been pointed at them. Every one of those encodes failed for
want of its input, the grab failed with them, and one episode reached the library. The download
folder was never touched: 37 GB, all nine files, intact.

`StageAsync` writes a file per episode, records **one** of them and answers with **one**. So the
tick's set of files something is waiting on held one path out of nine, and the sweep took the rest.
The store held one path too, which is the second half of the same fault: a restart re-asked all nine
episodes against the same video.

1. `StoredDownload.StagedPath` becomes `StagedPaths`. Newline-separated in the column it already has,
   because that is the one character neither platform allows in a path — and a row written by the old
   code carries no newline, so it reads back as the single path it always meant.
2. `StageAsync` answers with every file it wrote, and the tick spares all of them.
3. `FinishAsync` deletes all of them, rather than leaving eight for the sweep to puzzle over.
4. `AskAgainAsync` and `StillWaitingAsync` ask for each episode against **the file named for it**,
   never the first of them. `Landed.Wrote` decides which, which is the rule `S11-07` already reads
   the library with.

**Done when** a pack's staged files all survive the tick that staged them, and each episode asked for
again points at its own file. Read first: `Transfers.LeftBehindAsync`.

## S11-09 · A pack is encoded in episode order

Asked for by the owner while watching nine episodes queue as E06, E02, E07, E03, E08, E01. Nobody
watching a season wants that, and there was no reason for it: the order was simply the torrent's own,
because a pack lists its files however the uploader made it.

Staging and dispatch both walk what `Staging.Choose` answers, so that list is where the encoder's
queue order is decided.

1. `Choose` answers in episode order — season first, then the number, because a pack of two seasons
   is still one pack.
2. Nothing else moves. Each file is still matched to the episode named inside it and never by its
   position, which is the rule that keeps episode four out of episode one's slot.

**Done when** a pack listed in any order stages and dispatches from its first episode to its last.
Read first: `Core/Pipeline/Staging.cs`.

## S11-10 · Three pack faults, found by watching a pack go through

Nine episodes, seven landed, two died. The two are E01 and E05, and the chain that killed them starts
in this plugin three times over. All three are pack-only, which is why a grab of one episode has
never shown any of them.

**1. One job id for nine dispatches.** `EncodeJobAsync` was `UPDATE grabs SET encode_job = $job`, so a
pack kept the last id and threw eight away — verified in the owner's own row: 64 characters, one
Ulid. The plugin asks the server "is the encode still running?" through that column, so it was asking
about one episode out of nine. When that one finished it read the pack as finished and re-dispatched
all nine on top of the eight still running.

**2. One episode's failure failed the whole grab.** E01's encode died at 15:24:12 and the grab went
with it, which is what made the eight good ones stop counting as work in progress.

**3. The sweep took an input from under a running encode.** At 15:25:13 the server logged *"Encode
finished in 84.3s"* — E05's **first bundle**. At 15:25:15 the plugin cleared E05's input, because a
failed grab is waiting on nothing. At 15:34:50 the second bundle failed: *"Input file not found"*.
`VideoEncodeJob` opens its input once per bundle, and nothing in the plugin knew that.

1. Each dispatch writes its job against the episode it was for — `showXseasonXnumber:job`, space
   separated, added rather than replacing. An untagged id from a row written before this still
   answers for the whole grab, which is what it always meant.
2. A failed encode costs its own episode: `UncoverAsync` takes it off the grab so it goes back to
   missing and can be looked for again, the rest of the pack carries on, and only a release whose
   every episode failed is refused for six hours.
3. The sweep never takes a file while the server says a job that staged it is queued or running —
   whatever became of that file's grab. That is the belt to the braces of (2): even a grab that has
   properly finished cannot have its input pulled out from under an encode.

**Done when** one failed episode of a pack costs that episode and nothing else, and no file is ever
deleted while an encode is reading it. Read first: `docs/plan/PROGRESS.md` § Log, `S11-08`.

## S11-11 · The plugin cannot hold the server up

The owner said the plugin was hanging their server, repeatedly, from the first deploy onwards. It was
read as load — a verification pass and seven Chrome processes on a busy machine — and it was not that.

`TorrentRun.Session()` opened a torrent's session inside the run's own lock, and opening one calls
`Verified`, which falls back to `Hashed` when there is no resume file: a read and a SHA-1 of every
piece on disk. Thirty-seven gigabytes of that is minutes.

Everything that asks a run anything takes the same lock — `Progress`, `Torrent`, `Said`,
`SwarmSeeds`, `Paused`. `BittorrentEngine.StatusAsync` calls `Progress` for every torrent while
holding the engine's own lock. The Downloads page is rendered from `StatusAsync`, in the media
server's request thread. So the whole chain stopped, the dashboard's connection dropped and
reconnected, and the server looked hung — every restart, because a torrent with no resume file is
hashed again every time.

1. `Session()` decides what to open under the lock, reads the disk with the lock let go, and takes it
   again only to publish what it opened. A second caller arriving mid-pass is told there is no
   session yet, which every caller already handles, rather than starting a second pass over the same
   files.
2. `NothingWanted` asked for the session from inside the lock, which would have held it across the
   pass just as surely. It asks first and locks after.
3. The verifier is a seam so the rule can be stated: a test holds one open and asserts that every
   page-facing call still answers. It is a statement about locking, not about speed — with the lock
   put back the test cannot finish at all.

**Done when** every call a page makes answers while a torrent is being verified. Read first:
`src/NoMercy.Plugin.TorrentDownloader.Bittorrent/Engine/TorrentRun.cs`.

## S11-12 · A removed torrent takes its own files and nobody else's

The worst thing found in this sprint, and it had shipped in every build.

```csharp
Delete(held.Run.Folder(), infoHash);   //  Directory.Delete(folder, recursive: true)
```

`Folder()` is the folder **every** torrent downloads into. So finishing one grab, or the owner
cancelling one download, deleted every other download on the machine. On 2 September 2026 the owner's
download folder held two torrents' folders and three resume files; after one grab was finished with,
it held one folder and nothing else. Nothing irreplaceable went, because the others were already in
the library — with three downloads in flight it would have wiped two of them mid-download. It is also
why Dark Matter started downloading again after an unrelated grab finished: its files had been taken
by that grab's cleanup.

1. What belongs to a torrent is its own file list. A torrent of several files owns the folder of its
   own name and everything under it — including whatever the release shipped that was never
   downloaded, which is the owner's "no leftovers". A torrent of one file owns one file.
2. The download folder is never deleted, by anything, for any reason.
3. A torrent's name is not trusted with a path: the folder is resolved and checked to be under the
   download folder before a byte is removed, because a torrent that calls itself `..` otherwise names
   the owner's disk.
4. The resume and metadata files go with it, and the metadata is read before they do — it is what a
   torrent re-added from a magnet knows its own files by.
5. A torrent that never had metadata wrote nothing: the files are created when the session opens, and
   a session cannot open without the file list.

**Done when** removing one torrent with its files leaves every other download untouched and nothing
of its own behind. Read first: `BittorrentEngine.RemoveAsync`.

## S11-13 · The profile is applied to names before an indexer is asked

`docs/03-architecture.md` has described five stages since the first sprint:

```
  3 Judge the name    the profile applied to NAMES: slot, quality, codec, language, group, packs
  4 Find              every indexer in parallel
  5 Judge the copy    the profile applied to COPIES: seeders, size
```

Stage 3 was never wired in. `ReleaseFilter.JudgeName` exists and does exactly what stage 3 describes;
it was only ever called on the copies that came back from stage 4.

The owner watched four indexers being asked for
`South.Park.S15E12.1.Prozent.German.DL.AC3D.1080p.BluRay.x264-JaJunge` with English only on. It is a
correct scene release and a real PreDB name — a name database indexes every language — and their pool
holds 2,238 names in languages the profile refuses, 1,803 of them from srrDB. Each one cost a paced
request at every indexer carrying that show, and each one ate a place in `MaxSearchAttempts` that a
wanted name could have had.

1. Every name from the sources goes through `JudgeName` before an indexer is touched. The refusals
   are kept, so an episode where every name was refused says that rather than reading as a search
   that found nothing.
2. Only what survives is put to the indexers.
3. The terms this plugin makes up — the programme, the season, the episode as this plugin spells it —
   used to be asked first and always. They are the fallback now: asked when the sources gave no name
   the profile will have, or when the names they gave found nothing anybody is serving. Both happen —
   an episode nobody pre'd has no name to search for at all.

**Done when** no request carries a name the profile refuses, and the terms this plugin makes up are
asked only after the source's own names have had their turn and come to nothing. Read first:
`docs/03-architecture.md` § Stages.

## S11-14 · A refusal says which refusal it is

The owner's *Run now* button answered 404 on 1 September 2026 and worked again later with nothing
changed but restarts. Establishing which of three things it was took most of a day, because from
outside they are one answer:

1. the route was never registered — the server's business;
2. the plugin is not loaded at all;
3. the plugin is loaded, and it is a different type to the runtime than the endpoint was compiled
   against.

Every endpoint in both controllers answered `NotFound()` with no body, and an empty 404 is exactly
what a route that does not exist looks like.

The third is the one nobody guesses, and it fits what was seen. A plugin updated while the server
runs is staged beside the old copy rather than unpacked over it — media-server #29 — and a type from
one load context is not the same type as the identically named one from another. So
`GetPluginInstance(id) as TorrentDownloaderPlugin` answers null against an instance that is sitting
right there, and only a restart makes them one type again.

1. `Controllers/LivePlugin.cs` answers with the plugin or with the reason there is none.
2. All thirteen refusals carry it. The third names both types and says that a restart settles it.
3. The status code does not change: what changes is that the answer says which of the three happened.

**Done when** no endpoint here can answer 404 without saying why. Read first: nothing.

**Still not proven:** which of the three the owner's 404 was. The server logs no requests, so there is
nothing left to read. The next one will say so itself.

## What is not this repository's, and is written down so it is not looked for here again

Both were found while doing the above and neither has a fix that belongs in this plugin.

- **South Park S15E12 is attached to the wrong episode.** Written up in full for the media server as
  `docs/issues/media-server-post-encode-registration.md`, with the two lines that cause it, the
  duplicate that comes with it, and three fixes in order. Confirmed in the owner's own `media.db`:
  the `VideoFiles` row for `/South.Park.(1997)/South.Park.S15E12/South.Park.S15E12.1%.NoMercy.m3u8`
  names episode `153785` — season 0, "Chef Aid: Behind The Menu" — and there are two of it. Episode
  `153823`, the real S15E12, has none. The plugin dispatched the right id and the encoder wrote the
  right file; the post-encode registration is what put it on the wrong row. `S11-07` stops the plugin
  waiting six hours and re-downloading over it, and says so on the page — but the row is still wrong,
  and correcting it is the media server's.
- **A magnet pasted into the Downloads page is cut at 1024 characters.** Not here: the field is a
  `PluginFormFieldType.Text` with no length on it, `PluginFormField` in the contract carries no
  length, and the dashboard's `PluginForm.vue` renders a bare `<input>` with no `maxlength`. It is
  somewhere between the browser and the action payload, and it is outside this plugin either way.

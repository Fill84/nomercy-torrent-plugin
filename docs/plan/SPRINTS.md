# The plan

Nine sprints, forty-seven slices. Every sprint ends with something that can be judged. Every slice
ends with a green suite and a local commit.

Slice ids are stable. `PROGRESS.md` points at one and nothing else decides what is next.

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
1. Create the solution and six projects; wire the references.
2. Write `Directory.Build.props` and `.editorconfig`; introduce one deliberate style violation, see
   the build fail, remove it.
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
- `src/…/plugin.json` — per `docs/01-plugin.md` § Manifest, `network.hosts` empty for now
- `src/…/TorrentDownloaderPlugin.cs` — `IPlugin`, `IScheduledTaskPlugin`, `IUiPlugin`; four jobs with
  the defaults from `docs/04-domain.md` § Settings
- `src/…/Views/Pages.cs`, `tests/…Tests/ManifestTests.cs`, `TestSupport/FakePluginContext.cs`

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
`Download`, `Dispatch`), `ActivityEvent.cs`, `IActivityJournal.cs`, `ActivityJournal.cs`,
`ActivitySnapshot.cs`

**Steps**
1. Test: 1000 events from `Parallel.For` produce a snapshot with no torn state.
2. Test: a subject that starts and finishes is not in the in-flight list.
3. Test: history is bounded — 600 events leave 500.
4. Test: a snapshot already handed out does not change when the journal does.
5. Implement.

## S0-04 · Pushing the snapshot, and the dashboard

**Read first:** `docs/08-ui.md`.

**Files:** `src/…/Hosting/LiveSnapshot.cs`, `src/…/Views/DashboardView.cs`,
`tests/…Tests/Views/DashboardViewTests.cs`

**Steps**
1. Test: an idle snapshot renders a status bar saying when the last cycle ran and when the next is
   due — not a spinner, not an empty state.
2. Test: two in-flight episodes render two rows in **Now** with their stage and what each waits on.
3. Test: ten journal changes in 100 ms produce one push.
4. Test: a push that throws does not propagate.
5. Implement.

**Done when:** the dashboard is a pure function of the snapshot, with no store access of its own.

## S0-05 · Settings

**Read first:** `docs/08-ui.md` § Settings, `docs/04-domain.md` § Settings.

**Files:** `Core/Domain/Profile.cs`, `Cadences.cs`, `ClientLimits.cs`,
`src/…/Configuration/Settings.cs`, `SettingsStore.cs`, `src/…/Views/SettingsView.cs`,
`src/…/Controllers/SettingsController.cs`

**Steps**
1. Test: every setting in `docs/04-domain.md` § Settings round-trips with its documented default.
2. Test: an invalid cron is refused with the reason and the stored value is unchanged.
3. Test: an unwritable folder is refused with the reason.
4. Test: incomplete and intake on different volumes saves, with a warning on the page.
5. Test: a stored passkey and a stored API key render as *set*, never as their value.
6. Implement, including Run, Stop and the dry-run switch, which at this point do nothing and say so.

---

# Sprint 1 — Library and the missing list

## S1-01 · Reading the library

**Read first:** `docs/02-library.md`.

**Files:** `Core/Domain/Show.cs`, `Episode.cs`, `EpisodeKey.cs`, `LibraryKind.cs`,
`Core/Ports/ILibrary.cs`, `src/…/Hosting/HostLibrary.cs`,
`tests/…Core.Tests/TestSupport/FakeLibrary.cs`

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
7. Implement the adapter; it maps and nothing else.

## S1-02 · The missing list

**Read first:** `docs/02-library.md`, `docs/04-domain.md` § Episode states and § Storage schema,
`docs/10-known-failures.md` § B1, B2, B5.

**Files:** `src/…/Storage/Database.cs`, `Migrations/001-initial.sql`, `EpisodeRepository.cs`,
`Core/Pipeline/MissingRefresh.cs`

**Steps**
1. Test: an episode with a null or future air date is `NotAired` and never searched.
2. Test: an aired episode with no file is `Missing`, however old — an episode from two years ago
   counts the same as last night's.
3. Test (**B5**): a show that has ended is included, not skipped.
4. Test (**B1**): an `Unavailable` episode returns to `Missing` on a refresh.
5. Test (**B2**): a failed grab does not increment attempts.
6. Test: attempts and last-searched survive a refresh for rows that still exist; a row for an episode
   the library no longer has is deleted.
7. Test: the migration runner is idempotent.
8. Implement against a real temp-file SQLite database.

## S1-03 · Anime numbering

**Read first:** `docs/02-library.md` § Anime numbering.

**Steps**
1. Test: absolute of S02E13 after a 24-episode season one is 37.
2. Test: season 0 never counts.
3. Test: a season with a gap still numbers from the episodes that exist.
4. Test: the map is built from the episode list already fetched — no extra library call.
5. Test: a show in a `tv` library has no absolute number.
6. Implement.

## S1-04 · Shows and Queue pages

**Read first:** `docs/08-ui.md`, `docs/10-known-failures.md` § G3.

**Steps**
1. Test: the Shows page's missing count equals the rows for that show, from a seeded store.
2. Test: the library type is rendered per show.
3. Test: the Queue page separates *looking* from *waiting to air* and never counts an unaired episode
   as missing.
4. Test: the order is the order they will be asked in.
5. Implement.

**Sprint 1 done when** the Shows page matches a hand-counted library.

---

# Sprint 2 — Sources and fetch

## S2-01 · The catalogue and the host gate

**Read first:** `docs/05-sources.md`, `docs/10-known-failures.md` § C1, C2, B3.

**Files:** `src/…/sources.json`, `Core/Sources/SourceDefinition.cs`, `SourceCatalogue.cs`,
`SourceRole.cs`, `HostGate.cs`, `src/…/Configuration/CatalogueLoader.cs`,
`src/…/Hosting/HostGrants.cs`

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
   neither.
7. Implement.

## S2-02 · Fetching

**Read first:** `docs/05-sources.md` § Fetching, `docs/10-known-failures.md` § G1.

**Files:** `Core/Sources/IFetch.cs`, `FetchFailure.cs`, `src/…/Hosting/ChallengeAwareFetch.cs`,
`CloudflareChallenge.cs`, `ClearanceStore.cs`

**Steps**
1. Test (**G1**): a refusal names the address and blanks anything matching
   `api_?key|apikey|passkey|token|secret|rss_?key`.
2. Test: a gated host never makes an HTTP attempt.
3. Test: a challenge is solved once; a second after a fresh solve gives up with a clear sentence.
4. Test: clearance is spent on refusal, not trusted until expiry.
5. Implement. `LastBody` is exposed for the health tool and cleared by the caller.

## S2-03 · The hidden stage, and Chrome

**Read first:** `docs/07-solver.md` § No window and § The browser, `docs/10-known-failures.md` § D3, C5.

**Files:** `src/…/Solver/IHiddenStage.cs`, `WindowsDesktopStage.cs`, `XvfbStage.cs`,
`HiddenStages.cs`, `BrowserInstall.cs`

**Steps**
1. Test (**C5**): across two starts the downloader is called once.
2. Test (**D3**): `CanHideABrowser` is false on macOS, and a stage that cannot hide reports the reason
   and starts nothing.
3. Test: the stage is created **before** the browser process starts, asserted on a recording stage.
4. Test: a second `StartAsync` reuses the running browser.
5. Implement. Headless is not used.

## S2-04 · The solver

**Read first:** `docs/07-solver.md`, `docs/10-known-failures.md` § D1, D2.

**Files:** `Core/Sources/IChallengeSolver.cs`, `IPageSource.cs`, `IInPagePost.cs`, `Clearance.cs`,
`src/…/Solver/BrowserSolver.cs`

**Steps**
1. Test (**D1**): a JSON body fetched through the solver is raw JSON; the fixture is Chrome's viewer
   markup, so a DOM-scraping reader fails this test.
2. Test (**D2**): a navigation during the poll throws `Execution Context was destroyed`; the poll
   catches it and continues.
3. Test: one reload, then a sentence naming the host.
4. Test: two requests to one host share one tab; two hosts get two.
5. Test: clearance is kept per host with its user agent, and spent on refusal.
6. Test: `PostAsync` returns null when no solver can, and the caller says "this site needs a browser".
7. Implement.

## S2-05 · Readers, part one

Generic, 1337x, EZTV, KickassTorrents, LimeTorrents.

**Steps**
1. Capture a real page per site into `tests/fixtures/` with `tools/Capture`.
2. One test per reader: non-zero row count, first row's title and detail address asserted.
3. Test (**E4**): the Kickass fixture is the redirected release page; the reader yields one row with a
   magnet.
4. Test: EZTV's `[eztv.re]` suffix is stripped.
5. Test (**C4**): every reader name the catalogue uses resolves to a non-generic reader.

## S2-06 · Readers, part two

TorrentBay, TorrentGalaxy, Torrentz2, TorrentDownloads, TorrentFunk.

**Steps**
1. Fixtures for all five.
2. Test (**D4**): TorrentBay's reader finds rows on the real capture — `static readonly Regex`.
3. Test: TorrentBay's signed body is built from the four values; a row missing any is not asked.
4. Test (**E6**): TorrentGalaxy — several bare hashes yield none, exactly one yields it.
5. Test (**E1, E2**): TorrentFunk — bare attributes parse, and the title keeps its group.
6. Test: Torrentz2's foreign-site prefix is cut, anchored on ` - `.
7. Test: TorrentDownloads skips the advert row by matching the numeric id.

## S2-07 · JSON and XML sources, owner sources, and the health tool

**Steps**
1. Fixtures and readers for apibay, eztv-api, srrdb, nyaa, predb, scnsrc.
2. Test: srrDB honestly answering zero is not a failure.
3. Test: an owner-configured torznab source is built, asked, and its API key never appears in a log
   line or an error message.
4. Build `tools/SourceHealth` per `docs/05-sources.md` § The health tool.
5. Test (**G2**): the captured body is cleared between sources; a rate-limited source is retried once
   and reported distinctly.
6. Test: a page with more than two release-shaped links and zero rows read is reported as a broken
   reader.

**Sprint 2 done when** `dotnet run --project tools/SourceHealth` reports all seventeen answering.

---

# Sprint 3 — Names

## S3-01 · Parsing release names

**Read first:** `docs/04-domain.md` § Release names, `docs/10-known-failures.md` § H3.

**Steps**
1. Fixture: a file of real release names, scene and anime, taken from the captured pages.
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

## S3-03 · Resolving a name for an episode

**Steps**
1. Test: an episode answered by the pool costs **zero** requests.
2. Test: a miss asks the name databases once per (show, season) — two episodes of one season cost one
   query, asserted on a recording fetch.
3. Test: a show needing its year is asked under both forms and the answers are pooled.
4. Test: a show from an `anime` library is asked under both the seasonal and the absolute form.
5. Test: forty-two episodes across six seasons cost six queries.
6. Implement.

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
4. Test: metadata not arriving within `MetadataTimeoutMinutes` fails the torrent, blacklists the hash,
   and returns the episode to missing.
5. Implement.

## S5-08 · Encryption

1. Test: the MSE handshake against a captured exchange — DH key agreement, the `req1`/`req2`/`req3`
   hashes, and the RC4 keystream discard of 1024 bytes.
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
6. Test (**F4**): a torrent that finished while the server was down is staged on the first transfers
   tick.
7. Test: no progress and no peers for `StallMinutes` is stalled; progress without peers is not, and
   peers without progress for a minute is not.
8. Test: UPnP is tried, NAT-PMP is the fallback, and a failure is reported on the Settings page while
   the client carries on.
9. Implement.

**Sprint 5 done when** a real magnet downloads to completion, survives a restart mid-flight, and the
dashboard shows it the whole way.

---

# Sprint 6 — Grab, staging, dispatch

## S6-01 · The grab

1. Test: a grab records the release, hash, source and every episode it covers.
2. Test: a grab the engine refuses is recorded as failed in the engine's own words and does not burn a
   search attempt (**B2**).
3. Test: the merged trackers and `DefaultTrackers` both travel with the request.
4. Test: free space is checked first, and a refusal names how much was needed and how much there is.
5. Implement.

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

1. Fixtures: real Nyaa pages and fansub release names.
2. Tests for the anime grammar rows, including `v2` superseding `v1`, a title containing a dash, and
   `[Group]` extraction.
3. Test: a batch (`01~12`, `Complete`) is recognised as a pack.
4. Implement.

## S7-02 · Dual-form search

1. Test: an episode from an `anime` library is searched under `S02E13` **and** `- 137`, pooled and
   judged together.
2. Test: Nyaa is ranked first for an anime show and not asked for a television one.
3. Test: an absolute-numbered release maps to the right (season, episode).
4. Implement.

## S7-03 · Anime end to end

1. Test: a seeded anime library with a Nyaa fixture produces a decision for a missing episode.
2. Test: the Shows page shows the library type per show.
3. Implement, and run a real dry run over an anime library.

---

# Sprint 8 — Finish

## S8-01 · The remaining pages

Downloads, History, Skipped, Sources per `docs/08-ui.md`, each with a test that renders from a seeded
store and asserts the numbers (**G3**, **G4**).

## S8-02 · The remaining actions

1. Test: `SearchNow` searches one episode immediately and the journal shows it.
2. Test: `PauseDownload` and `ResumeDownload` reach the engine and the page reflects the state.
3. Test: `CancelDownload` stops it, removes it and returns the episode to missing.
4. Test: `AddTorrent` accepts a magnet and a `.torrent` URL, and a bad one is refused with the reason.
5. Test: `AllowRelease` grabs a release the profile had refused and records an `allowed` history entry
   naming the original reason.
6. Implement.

## S8-03 · Health automation

1. `tools/SourceHealth` gains a baseline file and the "fewer rows than last time" rule.
2. It exits non-zero when anything is broken.
3. README documents handing the report and the captured page over for repair.

## S8-04 · Hardening

1. Test (**F1, F2**): a run started with an already-cancelled token still runs; the endpoint answers
   before the work is done.
2. Test (**F3**): a tick arriving while its own cadence runs is dropped and logged.
3. Test: Stop cancels the cycle and leaves transfers running.
4. Test: a restart mid-cycle resumes without re-harvesting and without double-grabbing.
5. Test: shutdown waits briefly for a manual run and does not report it as a fault.
6. `scripts/deploy-to-server.ps1` — stopped-server check, base64 over ssh, hash verification of every
   file including `sources.json`.

## S8-05 · Release 0.4.0

1. README: what it does, how to build, how to deploy, how to read the health report.
2. Version bump in the places that carry it, with the test that they agree.
3. Deploy (owner stops the server, we deploy, owner starts it).
4. **Prove one real cycle**: a missing episode found, downloaded, staged, and an encode queued, with
   the log and the dashboard as evidence.
5. Tag `v0.4.0` locally. Push only when the owner asks.

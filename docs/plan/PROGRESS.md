# Where the work is

Read this first, update it last. Nothing else decides what happens next.

## Current

**0.3.16 is tagged. S10-09 is next, and it is not this repository's to start.**

**What 0.3.9 is: the chain closed, and the audit closed with it.** On 25 August 2026 Sugar S02E04
was downloaded, staged, dispatched with its own episode id and encoded into the owner's library at
22:33 — the first episode this plugin has delivered end to end. `docs/plan/AUDIT-0.3.9.md` is a full
read of the source made the same day, and Sprint 10 closed all eleven of its findings. **Not one of
them changed what the plugin does**: each removed work, moved a rule to one place, or put a seam
where the next change already lands.

What that came to: the rule for whose show it is written once instead of twice; a tick that asks the
library each question once instead of eight times; a database that prepares its file once a run
instead of 21,600 times a day; every piece of periodic housekeeping in the cadence named for it;
nothing in `Ui` that no page draws; and the encode behind a port, so the day the contract lands is
an addition rather than surgery.

**What 0.3.9 is not.** It still reaches into the server by name — five types, in one file. It still
decides a show is the owner's by whether it has a file on disk, which makes a show just added
invisible. Both are known, both are written down, and both are waiting on the media server.

**0.4.0 is not a date.** It is the version where this plugin stops reaching into the server by name,
and it waits on media-server #30, #34, #35, #36 and #37 — none of them this repository's to close.
S10-06 and S10-01 exist so that day is two additions rather than surgery. **S10-09**, and only when
the owner asks.

**0.3.9 is on both forges, built by CI from the tag.** The same package, the same bytes, published
by `.forgejo/workflows/build.yml` on the `v0.3.9` tag — forgejo builds it and writes the release to
forgejo and to GitHub. Nothing about a release is made by hand any more.

Getting the first CI this repository has ever had to green found four faults, three of them real:
`fetch-abstractions.sh` packed two of the contract's four packages and defaulted to a branch pinned
at a version that never moves — both invisible on a machine whose `_nupkgs` was already warm;
`EpisodeName` asked the operating system which characters a file name may not carry, so a Linux
server wrote names no Windows client could open; and the workflow's own `on.push` carried
`branches: ['**']` beside `tags: ['v*']`, which ran on every branch and silently never fired for a
tag, so a tag could be pushed with CI green and no release built anywhere.

**The branch is `master`.** The refactor was `full-clean-refactor` until 25 August 2026; the old
plugin's `master` is kept by the `v0.2.0` tag alone, which is what a tag is for.

**Still to do, and neither is this repository's code.** Forgejo does not push refs to GitHub by
itself — a push mirror has to be set on the repository, or the two only stay level because a person
pushes to both. And the plugin has never been built or run on Linux beyond CI.

**0.3.9 was published on GitHub by hand first**: `NoMercy.Plugin.TorrentDownloader-0.3.9.zip`, on the owner's ask of
25 August 2026. **Not yet on forgejo, which is where the releases live** — see the S10-08 entry. The `v0.4.0` tag that stood in the way is gone. It named `ecc0241` of 21 August — 74 commits behind,
older than every fix of the week that followed, and never published as a release; only `v0.1.0` ever
was.

The section below is what was true **before 25 August 2026**, and is kept because it is what the
proving looked like. Read it as a record, not as a statement of what holds now: the encode it says
has never happened has since happened, and the library rule it lists as done was reverted.

**Everything up to staging is proved on the owner's own server. The step the plugin exists for —
asking the encoder, and knowing what became of that — has never once succeeded there.**

Between 22 and 24 August 2026 the plugin ran on `beast-unit` through several restarts and the whole
of the chain was watched rather than trusted. It picks the right release, refuses what it should,
downloads at full speed, stages the episode into the intake folder, and gives nothing back to a
public swarm. Twenty-three grabs reached `done`, the first that had ever existed.

What is proved on real data:

- The right release, from the name sources rather than an indexer's rendering.
- h265, 2160p and foreign audio refused, each with a reason on the Skipped page.
- A 1.2 GB executable named after an episode **refused before a byte of it was fetched**.
- Downloads at 7.2 MB/s where they had sat at nought, and no upload at all on a public torrent.
- Five faults that each alone stopped the episode ever reaching the library, all found by watching:
  the stager asking for a share mode Windows refuses while the client holds the file; the delete
  afterwards counted as a failure of the copy; a multi-file torrent's video looked for in the wrong
  folder; the encode dispatch resolving an ambiguous overload and writing a string into a `Ulid`;
  and the resume file that was read on every start and written by nothing at all, so every restart
  re-downloaded everything.

**What has never happened: one `encode dispatched` on the real server.** That is the whole of what is
left. Sprint 9's other five slices are done — the contract moved to the released version, the
buttons live in the table row, every show in a library was put in scope whatever it had on disk
(**reverted the same afternoon — see S9-03 and § Decisions**), an episode left in the intake folder
is dispatched anyway, and a torrent still seeding is not cleared up under it.

Until one episode has gone from missing to in the library with nothing done by hand, 0.4.0 does not
go out.

## Blocked

**Nothing is blocked on a deploy any more.** That paragraph said the first install went over with
every hash matching and the plugin still could not load. The cause was found and fixed on 21 August
2026, it has deployed cleanly on every stop since, and on 25 August it delivered an episode end to
end. What is left below is watching, not fixing — three acceptances that were written to be observed
on the owner's own library and never have been. Each needs a deploy and a look, and the owner starts
and stops the server.

- **Sprint 4's acceptance on the real library.** The chain decides end to end and is proven against
  real captured pages, but "the dashboard shows what it would take for every missing episode" has not
  been watched on the owner's library. It runs with no torrent client and says so per episode, which
  is exactly what dry run shows; say when, and it can be deployed and watched.
- **Sprint 7's acceptance: a real dry run over the real anime library.** The chain decides for an
  anime episode against a captured Nyaa page and the absolute number is derived from the library
  itself, but "it works over the owner's own anime library" has not been watched. Dry run hands
  nothing to the client, so it is the safest of the three to watch first.
- **Sprint 1's acceptance against the real library.** `HandCountedLibraryTests` proves the chain
  against a library counted by hand, but "the Shows page matches *the* library" has not been checked
  against the real one. Say when, and it can be held up to the ~25 shows and ~42 missing episodes
  recorded under **Facts**.

**Both of the owner's outstanding decisions are made.** Trackers are learned (see **Decisions**). The
`v0.4.0` tag was the other, and it is taken back by **S10-08**: it names `ecc0241` of 21 August, 74
commits behind and older than every fix of the week that followed.

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
- [x] `S1-04` Shows and Queue pages

### Sprint 2 — Sources and fetch
- [x] `S2-01` The catalogue and the host gate
- [x] `S2-02` Fetching
- [x] `S2-03` The hidden stage, and Chrome
- [x] `S2-04` The solver
- [x] `S2-05` Readers, part one
- [x] `S2-06` Readers, part two
- [x] `S2-07` JSON and XML sources, owner sources, and the health tool

### Sprint 3 — Names
- [x] `S3-01` Parsing release names
- [x] `S3-02` Harvest
- [x] `S3-03` Resolving a name for an episode

### Sprint 4 — Find and decide
- [x] `S4-01` The profile
- [x] `S4-02` Find
- [x] `S4-03` Deciding
- [x] `S4-04` The pipeline end to end

### Sprint 5 — BitTorrent
- [x] `S5-01` Bencode
- [x] `S5-02` Torrent metadata and magnets
- [x] `S5-03` The engine shell and its port
- [x] `S5-04` Trackers
- [x] `S5-05` Peer wire
- [x] `S5-06` Pieces, verification and disk
- [x] `S5-07` Metadata from peers
- [x] `S5-08` Encryption
- [x] `S5-09` DHT
- [x] `S5-10` Peer exchange and local discovery
- [x] `S5-11` Rate limits, choking and seeding
- [x] `S5-12` Resume, recovery, stalls, pause and ports
- [x] `S5-13` The client, joined up
- [x] `S5-14` The engine drives the session

### Sprint 6 — Grab, staging, dispatch
- [x] `S6-01` The grab
- [x] `S6-02` Completion and staging
- [x] `S6-03` Encode dispatch
- [x] `S6-04` Downloads page and history

### Sprint 7 — Anime
- [x] `S7-01` Anime naming
- [x] `S7-02` Dual-form search
- [x] `S7-03` Anime end to end

### Sprint 8 — Finish
- [x] `S8-01` The remaining pages
- [x] `S8-02` The remaining actions
- [x] `S8-03` Health automation
- [x] `S8-04` Hardening
- [x] `S8-05` Ship it: a README, one version, and a deploy that works

### Sprint 9 — Finishing it
- [x] `S9-01` Build against the contract that ships
- [x] `S9-02` The buttons live in the table
- [x] `S9-03` Every show in a library is in scope — **done and reverted the same day**, see below
- [x] `S9-04` Prove the encode end to end
- [x] `S9-05` What was left behind
- [ ] `S9-06` Release 0.4.0 — **superseded by** `S10-09`

### Sprint 10 — What the audit found
- [x] `S10-01` One rule for whose show it is
- [x] `S10-02` One question, one answer, per tick
- [x] `S10-03` A connection costs a connection
- [x] `S10-04` Maintenance does maintenance
- [x] `S10-05` Nothing that nothing reaches
- [x] `S10-06` A port for the encode
- [x] `S10-07` The plan says what happened
- [x] `S10-08` Release 0.3.9
- [ ] `S10-09` Release 0.4.0 — on the contract, with no reflection left

## Log

One line per finished slice: the id, what landed, and anything the next slice should know.

- **0.3.18.** Proved against the owner's own pack, and the pipeline pinned from both sides. The
  season-pack path was built and never run against anything a scene group actually publishes, so the
  nine file names of the Dark Matter pack are a fixture now — episode titles and all, which is where
  a parser expecting tags meets `Are.You.Happy.in.Your.Life` — and the test reads them into their
  nine episodes and stages each under the episode that names it. The pipeline depth asserted exactly
  rather than at most: a cap that slipped to one piece would hold no memory either and would quietly
  ask a peer for sixteen kibibytes at a time. Bytes in flight to one peer are the pipeline times the
  piece length, megabytes on any real torrent.

- **0.3.18.** Every peer joins the download, not only the one that described it. The metadata fetch
  waited on its own peer saying something, and a seed says nothing: it has every piece, so no
  `have`, and it will not unchoke a client that never said it was interested — which this client
  could not say until it was in the session. Keep-alives do not wake it either; the connection
  swallows those. So only the peer that happened to deliver the last block of the metadata ever
  reached the session, and every other one sat in that read being asked for nothing until the far end
  dropped it. **This is why the season pack fetched its metadata at 05:15 and was at nought peers by
  05:29 with hundreds of seeds in the swarm** — the unanswered question from 0.3.17. The fetch now
  waits on the metadata as well as on the peer, and hands its half-finished read to the session
  rather than abandoning it. A peer that cannot serve the metadata at all is held until somebody else
  does, instead of being dropped for not being able to describe a file it has every piece of.

- **0.3.18.** A torrent added by hand reaches the library. docs/08-ui.md said all along that
  `AddTorrent` still runs the finished file through staging and the encode dispatch, because a
  torrent added by hand is an episode like any other. Half of it was built: the grab is recorded
  covering no episode, deliberately, since claiming one nobody chose would put that episode back to
  missing if it failed — but nothing ever worked out what it turned out to hold, and `Staging.Choose`
  is handed the episodes and returns nothing when there are none. So a magnet pasted in downloaded in
  full and stopped: 37 GB of Dark Matter sat complete with nothing able to move it. `Staging.Discover`
  reads the episodes out of the torrent's own file names, matched against the shows the server offers,
  and they are written to the grab because every step after staging reads them back from the store.
  Pack or single episode, both go the same way. Nothing is guessed: a video naming a show the owner
  does not have, or no episode at all, is left where it is and said so in the journal.

- **0.3.18.** No piece is held in memory. A piece was assembled in a buffer its whole length and
  written only once its hash matched, so every piece being built cost that buffer. With four pieces
  claimed per peer and fifty peers that is two hundred at once — over a gigabyte on a season pack
  whose pieces are megabytes each, even after the per-peer count was fixed. A block now goes to its
  place on the disk as it arrives and the piece is hashed by reading it back; what is held is which
  blocks arrived, one bit each. Nothing unverified is ever served: `Serve` refuses any piece the
  verified bitfield does not have, and a piece that fails is fetched again over the top.

- **0.3.17.** Released. Two faults, one torrent. **Memory:** `Pipeline` is four and its summary says how many
  pieces are asked of one peer at a time, but it was counted per call, and the asking runs on every
  message a peer sends — so each message claimed up to four more pieces, each holding a buffer the
  size of a whole piece until it arrived, failed or sat unanswered for a minute. A peer that talks
  without sending blocks walked the client through the whole file list: a 36.1 GB season pack put the
  media server at 45 GB resident while showing nought per cent. The count is per peer now, and a
  peer that leaves stops counting.

- **0.3.17.** A torrent that loses its peers finds them again. Every address a run dialled went
  into a set nothing took it out of, so once the peers of the first announce were gone, every later
  announce named the same addresses and every one of them was refused: nought peers, nought seeds
  and nought per cent until the owner paused and resumed, which was the only thing that cleared the
  set. A season pack sat there on 30 August 2026 while qBittorrent saw three hundred seeds in the
  same swarm. The set is now a clock — an address not connected is offered again after thirty seconds
  — the run keeps an address book so a pass has somewhere to look without announcing, the announce
  keeps the tracker's own interval whatever the pass does, no pass dials past fifty peers, and a
  run with nobody to talk to comes round every minute instead of every half hour. Both numbers are
  the owner's. The Downloads page also says how many of the swarm this client is connected to
  rather than only its own count.

- **0.3.16.** Enough peers. Dht, PeerSearch, DhtStore, Pex, PeerExchange, LsdSocket and
  LocalDiscovery were all written, all tested, and none was ever constructed — so every peer this
  client had came from a tracker's fifty addresses, most of them stale, which is why a swarm with
  hundreds of seeds gave it one peer. `ut_pex` is asked for and dialled, the DHT is joined and asked
  on every announce pass over a transport that did not exist before, and the local network is
  announced to. Nothing is asked of the DHT before the metadata says the torrent is not private, and
  it searches without announcing. Also: the solver left open every tab it read a page in — ninety
  Chrome processes with nothing running — and a block nobody asked for is no longer written.

- **0.3.15.** Downloads start, and the gated indexers answer. The browser was stopped when its last
  tab closed, so 1337x, TorrentBay and EZTV each met their challenge from cold with none of the
  clearance the last solve earned — two of the three never got past it and all three reported no
  rows, which is why the site with the most seeders was never chosen. The job object added in 0.3.13
  is what keeps a killed server from leaving a browser behind, so the teardown went. Asked again:
  1337x 2 rows, TorrentBay 34, EZTV 5.
- **0.3.15 (the rest).** Downloads start. A client that takes a magnet on dials with nought pieces, because the
  piece count is what it is dialling for — and nearly every peer sends its bitfield the moment the
  handshake is done. A bitfield for nought pieces is nought bytes, so each one read as a protocol
  violation, the metadata fetch unwound, the conversation swallowed it as "one peer is one peer", and
  the peer was destroyed on its first message. 175 dialled, nine handshaken, nine gone. A bitfield is
  now taken as it comes until there is something to check it against. The swarm test finds peers in
  three seconds and the file appears on disk.

- **0.3.14.** The reason nothing downloaded. Before the metadata arrives nobody knows the size, and
  the announce sent `left = 0` — which is not "unknown" to a tracker but **seed**, and a seed is sent
  no peers. Every magnet announced itself as finished and was answered with an empty peer list and no
  error, which is exactly what "fetching metadata, 0 peers, 0 seeds" was. The same hash announced to
  the same tracker with this plugin's own code answers seeders 1206. `left` is a terabyte while the
  size is unknown: large as well as non-zero, so a tracker ranking by need does not read a client
  that knows nothing as nearly finished.

- **0.3.13.** Downloads that never started. Every name now goes to every indexer before anything is
  taken — two early exits gone, the shelf answer taken before any indexer was asked and the return
  from the name loop on the first copy worth taking — because a site only answers about the name it
  was asked, and one holding the release under another spelling was asked and never found it. The
  cap and the per-host gate still bound the cost. Merging is by hash as well as by name, so one hash
  is one torrent whatever a site calls it, carrying the best seeder count and every tracker. A grab
  that failed can be taken on again, which 0.3.12 had made impossible: its row is hidden from the
  page but not from the unique index, so a magnet pasted by hand was refused by a row nobody could
  see. And the plugin's own page is mounted on the dashboard, so Open and the title beside it go to
  one address rather than two.

- **0.3.12.** One torrent is one grab, enforced by the schema instead of swept up after the fact.
  The index on the hash was not unique, and a cycle records a grab per episode it decided, so a pass
  deciding the same episode twice wrote the hash twice. The rule lived in cleanups — a migration
  once, the maintenance cadence at every start — which is why three duplicates were cleared at a
  start on 25 August 2026 and three more were on the page the same evening. The insert now does
  nothing about a torrent already known, the first row wins, and the periodic sweep is gone because
  nothing can make a duplicate for it to find. A state of two words also reads as two words: the
  page said `fetchingmetadata`.
- **0.3.11.** Two faults that had been there for as long as the plugin worked. The snapshot is
  pushed once a second instead of four times: the floor was set for what a message costs the server,
  but the web app cannot read this plugin's payload — it draws every plugin — so it answers any
  message by re-reading the whole view over HTTP. A download in flight publishes on every tick, so a
  quarter of a second was four complete page reads a second. And the browser now dies with the
  server however the server ends: it is started suspended, put in a Windows job object with
  kill-on-close and then resumed, because every tidy-up before this one ran on the way out and a
  killed server runs none of them. Sixteen Chromes were found on the owner's machine with the server
  stopped.
- **Three faults in the web app, not here.** Plugin pages are capped at 64rem, so five of the
  Downloads table's nine columns cannot be reached on any display; a live push blanks the page to a
  spinner rather than swapping the tree, which is the other half of the flicker; and an undeclared
  route renders a dangling `nm-plugin-shell--` that matches no rule. Filed as
  NoMercy-Entertainment/nomercy-app-web#32, #31 and #33. **No version of this plugin can fix any of
  them** — there is no shape it can ask for that means full width.

- `S10-08` **0.3.9.** All three carriers say `0.3.9` — `Directory.Build.props`, `PluginIdentity` and
  `plugin.json` — and a test holds them together. The stale `v0.4.0` tag is deleted locally and on
  both remotes; it named `ecc0241` of 21 August and had never been published as a release, so
  nothing was withdrawn from anybody. `v0.3.9` names the commit that carries the number. What ships
  is the audited plugin, not the plugin that was about to be audited.

  **Released on GitHub on the owner's ask**, with
  `NoMercy.Plugin.TorrentDownloader-0.3.9.zip` — 44 files, 20 MB, sha256 `b7fd7c54…`, the digest
  GitHub recorded matching the one built here. It carries every assembly the plugin needs and SQLite
  for all twenty-one platforms, because a plugin folder missing its dependencies loads as nothing at
  all with nothing to say why: that is what happened on 21 August, when twelve assemblies were named
  and three were there. Symbols and documentation are left out; nothing at runtime reads them.
  **Not released on forgejo, and that is the one that matters.** `v0.1.0` and `v0.2.0` are both full
  releases there, with their zips; GitHub only ever carried `v0.1.0`. So forgejo is where this
  plugin's releases live and `v0.3.9` is missing from it. The tag is pushed; what is missing is the
  release entry and the asset. It needs a Forgejo token with write access to the repository — the
  credential stored for that host answers 401 to both basic auth and `Authorization: token`, so it
  has expired or been revoked.

  The release notes say what it does not do as plainly as what it does: it still reaches into the
  server by name, and a show just added is still invisible to it.
- `S10-07` **E1, E2.** Every slice marked done now describes what really happened. **S9-03** was the
  dangerous one: marked done, reverted the same afternoon, and left reading as instructions — a
  reader following the plan would have put the 479 grabs back. It now says what it cost, why the
  reasoning was sound and the premise false, and what replaces it (media-server #36 and #34, with
  S10-01 making that one line). `docs/02-library.md` said the reverted rule as well and now says the
  one the code applies. **S8-05** was called "Release 0.4.0" and released nothing under a number the
  plugin had not earned; it is "Ship it: a README, one version, and a deploy that works", and its two
  waiting steps landed on 25 August. **S9-06** is superseded by S10-09 rather than being a second
  slice of the same name. Also corrected: S9-04 was done and not marked, `docs/03-architecture.md`
  named a `Core/Transfers/` folder that has never existed (it is `Core/Ports/`, six interfaces), the
  Sprint 9 slices were never in § Slices at all, and `ILibrary.GetShowsAsync` said every show in
  those libraries is in scope, which is `Ownership.Theirs`'s question and not that port's.
- `S10-06` **F1.** `Core/Ports/IEncodeGateway.cs` — a staged file, the episode it is, the show it
  belongs to, where that show's episodes already are, and an answer of taken or not. `EncodeDispatch`
  implements it with nothing inside it moved; the one thing that did move is the `"anime"`/`"tv"`
  string, from the cadence into the adapter, because it is the file list service's vocabulary and not
  the domain's. `Transfers` now names no host type at all, and `EncodeDispatch` is named in exactly
  one place: the line where the plugin is composed. The answer stays a bool rather than a reason —
  the slice said "queued or a reason", but the caller acts the same way whatever the reason and the
  refusal is already logged and journalled where it happens, so a reason nobody reads would be the
  dead code S10-05 has just finished removing. The interface says instead that a refusal must say why
  before it returns. The test is the day itself, rehearsed: a second implementation handed to the
  same cadence, asked the same things. It could not even be compiled before this slice.
- `S10-05` **D1.** `Ui.List`, `Ui.Container` and `Ui.EmptyState` are gone, and so are the three
  client names only they used — recorded in `docs/08-ui.md` § Components instead, so a page that
  needs one has them without reading the client again. The finding's *reasoning* was wrong and the
  audit is corrected: no page draws an empty state by hand. Every "nothing here" is a table's own
  empty message through the one `Ui.Table` helper, and the two places that could have used an
  `EmptyState` carry a comment saying why they must not. `EveryHelperOnUiIsDrawnByAPage` fails when
  a helper nothing draws is added, which is what stops this coming back. `docs/08-ui.md`
  § Components also said the vocabulary was `PluginComponentType`, which is the one thing the code
  must never send — corrected while there. The BitTorrent client's three unused members were not
  touched: it is proven and out of scope.
- `S10-04` **C1.** The maintenance cadence does the maintenance: re-derive the missing list, prune
  old refusals, clear duplicate grab rows. `RefreshAsync` refreshes and nothing else, and search
  keeps its own refresh because a cycle needs a fresh missing list and must not wait for four in the
  morning. The `_refreshed` flag did **not** simply go, and the slice was corrected to say so — see
  **Decisions**. Each of the three pieces has a test that fails when that piece is deleted.
  `docs/01-plugin.md` § The four cadences now says what the cadences really do, and the slice's
  "Read first" pointed at `docs/04-domain.md` § Cadences, which has never existed.
- `S10-03` **B4, B5.** The data folder and `journal_mode` are done once per database file rather than
  on every call — per store, not static, because another store is another file whose journal mode
  nothing here has set; the plugin has one store, so once per store is once per run. `foreign_keys`
  is genuinely per connection and did not move. The settings are remembered as the JSON the host
  last gave, and the memory is dropped by a save that really wrote. Reading the server settled two
  things the audit only estimated: its `GetConfigurationAsync` is a file check, a full file read and
  a deserialise, behind a semaphore it shares with every other plugin — so the round trip is worth
  saving. It also settled the shape: a load hands back a **new object every time**, because the
  settings page loads, applies what was typed and saves nothing when a field is refused, and one
  shared object would leave the plugin running on values the owner was told were refused. Nothing in
  the suite caught that, so `ARefusedEditIsNotLeftBehindInWhatTheNextLoadGives` was written for it.
- `S10-02` **B1, B2, B3.** One tick asks the library each question once. `LibraryThisTick` wraps the
  port for the length of one pass and is then thrown away — a tick lasts moments, and an answer kept
  past it would be a decision made on what used to be true. A tick staging four episodes went from
  eight round trips for the shows to one, and the two separate caches of "does this show have a
  file" became one. Staging now returns the path it wrote, so the open grabs are read once a tick
  instead of twice. Two tests count calls rather than outcomes, which no other test here does, and
  they do it because the cost is the whole of the fault; dropping the just-staged paths from the
  waited set fails four existing tests, so the trap that second read existed for is still held.
- `S10-01` **A1.** The rule that decides whose show it is now exists once, in
  `Core/Pipeline/Ownership.cs`, and both the refresh and the transfers tick call it. It is the rule
  that put the plugin on 479 grabs when it was changed in one place on 24 August, and it is the rule
  that changes again when media-server #36 and #34 land — the comment on `Ownership.Theirs` says
  both. One test asserts both sides against one library: a show with nothing on disk is neither
  tracked nor left downloading, and the owner's own show is neither skipped nor cancelled. Each half
  was seen to fail on its own before the extraction, and the whole test fails whichever constant the
  body degenerates to. `docs/02-library.md` still describes the widened rule that was reverted; that
  is **S10-07**'s to correct, not this slice's.
- `S9-04` The encode is proved end to end. The job carried an empty media id, so the encoder threw it
  away in silence — `Id.ToInt()` on an empty string is 0, which matches no row. That id came from
  asking the server to identify the staged file all over again from its name. The plugin chose the
  show, the season and the number, so it now asks the server's own table for that row: media-server
  #35 puts it in the contract, and until then it is read through `MediaContext`, which is recorded
  under **Decisions**.
- `AUDIT` A full read of 27,241 lines of source before 0.4.0. Eleven findings, one of them high: the
  rule that decides whose show it is exists in two places. No `TODO`, no commented-out code, and
  twelve types named only in their own file, all of them legitimate. `docs/plan/AUDIT-0.3.9.md`.

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
- `S1-04` **Sprint 1 is done.** Shows and Queue render from the store, and every count is counted
  from the rows it summarises. The Queue's order is the search cadence's own rule, used by both, so
  the page states what the plugin will do rather than guessing at it. Three lists, not two: *given up
  for now* is its own, or an unavailable episode appears nowhere at all.
  `HandCountedLibraryTests` runs the whole chain — server-shaped library, adapter, derivation, SQLite,
  page — against a library counted by hand. `S2-01` starts on the sources.
- `S2-01` All seventeen sources ship in `sources.json`, written from `docs/05-sources.md`, and the
  catalogue is read from the **assembly's own folder** (**C1**). `HostGrants` asks for every host of
  every owner-configured source, search addresses included (**C2**). `HostGate` keeps one gate per
  hostname and owns backoff: a refusal widens, a success halves, and a permission refusal earns
  nothing at all (**B3**). `S2-02` puts a real fetch behind it.
- `S2-02` `ChallengeAwareFetch` is the whole of "whatever it takes": grant, gate, plain HTTP, browser
  for a gated address, challenge met once. Every failure names its address with secrets blanked by
  parameter name (**G1**) — by name and not by shape, because a passkey and an info hash are both
  forty hex characters. Clearance is spent on refusal, and a second challenge after a fresh solve
  gives up rather than looping. `S2-03` builds the browser it has been asking for.
- `S2-03` The stage exists before Chrome does, and only a stage can launch one — so the fault where a
  window appears on the owner's desktop for half a second cannot be written (**D3**). macOS has
  nowhere to hide a window, and that answer is asserted on a machine that is not a Mac by keeping the
  decision separate from asking the operating system. The browser is downloaded once and kept across
  restarts, and an install whose browser has gone is not an install (**C5**). Headless is never used:
  it does not pass a managed challenge. `S2-04` drives the browser it starts.
- `S2-04` `BrowserSolver` holds every judgement and is tested against a fake tab; `PuppeteerTab` only
  does as it is told. A JSON body comes back as JSON and not as Chrome's viewer for it (**D1**),
  decided by `document.contentType` rather than by the address. A navigation mid-poll is the page
  clearing itself, caught and carried through (**D2**). One reload, then a warning naming the host.
  One tab per host, because clearance is issued per host. `S2-05` needs real captures: it is the
  first slice that cannot be written without `tools/Capture`.
- `S2-05` (part) `tools/Capture` saves a real page through the same catalogue, fetch and solver the
  plugin uses. Four captures taken: 1337x, EZTV, KickassTorrents, LimeTorrents. Taking them found two
  real faults in `S2-04`'s solver — both fixed, one with a test. `Query` writes a term into an
  address per style (**E3**).
- `S2-05` Four readers against four real pages, and eleven of twelve mutations bite. EZTV's tag,
  Kickass's fragmented names and LimeTorrents' hashed `.torrent` link all behave as captured rather
  than as documented. A reader name nothing answers to resolves to nothing, never to the generic
  reader (**C4**) — silent fallback is the fault itself. `S2-06` writes the remaining four readers
  and follows a detail page to its magnet.
- `S2-06` (part) Five more captures taken, and a third real fault found by taking them: a navigation
  that never finishes threw straight out of the solver and took the caller down with it. Fixed, with
  a test.
- `S2-06` The five remaining readers, each against its capture. **C4 is now complete**: every reader
  name in `sources.json` resolves to a reader written for that site, read from the file that ships.
  TorrentBay's **signed POST moves to `S6-01`** — the endpoint is in an external script the page never
  names, and the reader's job is the row and the id, which it does. Fourteen rules, fourteen
  mutations, two of which found rules with no test at all.
- `S2-07` (part) Seven more captures and six data readers: apibay, eztv-api, srrdb, nyaa and the RSS
  feeds. srrDB encodes every dash in a release name, so `Html.Decode` now reads numeric entities —
  without it a scene name matches nothing at all. apibay says "nothing found" as a row saying so, and
  that row is not a release. An owner's Torznab key is sent and appears in no failure, message or log
  line. `tools/SourceHealth` is what remains.
- `S2-07` **Sprint 2 is done: sixteen of sixteen answering, nothing flagged.** `tools/SourceHealth`
  walks every enabled source through the same catalogue, fetch, solver and readers the plugin uses
  and writes `health/report.md` beside the page each one returned. The page is cleared **before**
  each ask rather than after (**G2**), because a run that throws leaves no way out — and a stale
  body is how one source's page came to be reported under another's name. A rate-limited source is
  asked once more, waiting out the gap the gate has just widened, and twice refused is reported as
  rate-limited and never as a broken reader. Zero rows off a page covered in releases is a broken
  reader; a source that was never read has no row count at all, because nought means read and empty.
  Running it found two real faults, both fixed with tests: every source with no `reader` field
  resolved to the reader for an HTML listing, and all four gated sites were sent down plain HTTP.
  `S3-01` starts on names.
- `S3-01` `ReleaseName.Parse` and `TitleMatcher.Matches` in `Core/Domain`, against every name on
  every captured page — eight hundred and sixty of them — and twelve named ones each proved to be a
  row a reader really read. Five more captures were taken because the field table's anime cases were
  on none of the old ones: absolute numbering, `v2`, a batch, a diacritic and a show called *Greek*.
  Taking them found two things no document had: `EP1173` with no separator anywhere, which is how
  One Piece is posted every week and which the parser now reads, and the same programme spelled
  *Pokémon* and *Pokemon* in one title, which is why accents are folded. `S3-02` harvests names into
  the pool.
- `S3-02` `Harvest` in Core reads every feed at its own address, all of them at once, and keeps what
  it finds in `name_pool` through `INamePool`. **A2** is one line: a feed is read at `Url` and never
  at its search address. A feed that refuses, and a feed that throws, each cost that feed and
  nothing else — 0.3.4 read them in turn inside one try block, so the first refusal ended the pass.
  Writing the pool key found a real fault: one Nyaa page carries one episode as
  `Frieren.Beyond.Journey.s.End`, `Frieren Beyond Journeys End` and `Frieren- Beyond Journey's End`,
  which are three keys and one episode. The key now runs the words together; matching still counts
  them, because there the gaps are what tell *Silo* from *Silos*. `S3-03` reads the pool.
- `S3-03` **Sprint 3 is done.** `NameResolve` asks the pool first and a name database only for what
  the pool cannot answer — and asks it **once per show and season**, so forty-two episodes across six
  seasons cost six questions per database rather than forty-two. A one-word title is asked with its
  year as well (*Sugar* answers with beekeeping, *Sugar 2024* with the programme), and an anime show
  is asked under the bare title too, because an absolute-numbered release carries no season tag at
  all. What comes back is written to the pool before it is used. `S4-01` judges the names.
- `S4-01` `ReleaseFilter` and `ReleaseDecider` in Core, every rule of the profile table with a test
  that fails when the rule is deleted — eighteen of them checked by hand. **A1** is structural: a
  name has no seeders and no site, so those rules live on `JudgeCopy` and nowhere else, and a profile
  wanting five hundred seeders still accepts the name it would refuse the copy of. Unknown is not
  nought either: a site that publishes no count has not said there is nobody there. The ranking is
  seeders first and site priority **descending**, which 0.3.4 had inverted. Writing the language rule
  needed the parser's vocabulary widened from four claims to thirteen — a release in German says
  `GERMAN` and nothing else — with a row of a real capture behind each, and still no Greek in it.
  `S4-02` asks the indexers.
- `S4-02` `Find` asks every indexer at once for the **full release name** (**A3**) and merges what
  comes back by info hash: the highest seeder count, the site that had it, and the trackers of all of
  them, because more trackers is a faster download and that is the whole reason every indexer is
  asked. A copy with no hash is never merged into anything — nothing says two rows with the same
  title are the same file. **C3** is answered by following the chosen copy to its own page, once,
  and only when it has no magnet: `tools/Capture` grew a `--page` form to take the two detail
  captures that proves it, one publishing a magnet and one printing a bare hash and nothing else.
  `S4-03` decides between what comes back.
- `S4-03` `Decisions` holds what one cycle has decided so far, which is where the rules that need
  more than one release live: a pack is worth its bytes only once the season's gaps reach
  `SeasonPackThreshold`, a pack that is taken settles every gap it covers so none of them is searched
  for again, and everything refused is kept with the episode, the site and the reason for the Skipped
  page. A name the profile refused is recorded with **no** site against it — nothing was asked, so no
  site refused anything. `S4-04` runs the whole chain.
- `S4-04` **Sprint 4 is done.** `SearchCycle` runs names, search, decision and grab for every gap in
  queue order, and reports one outcome per episode with the release, the site, the count and a reason
  in words. `Chain` in the shell assembles what talks to the outside — catalogue, gate, grants,
  browser, solver, pool — once, and the feed and search cadences run through it. Three real faults
  came out of joining the parts up: a season pack was pooled under its season and looked up by
  nobody, so no pack could ever be taken; nothing checked that a copy a site answered with was a copy
  of what was asked for; and a copy carrying a hash was followed to its own page for a magnet it
  already had. All three are fixed with tests. `S5-01` starts the torrent client.
- `S5-01` Bencode reads and writes over `ReadOnlySpan<byte>`, against Ubuntu's own published torrent
  — a real file with 484 KB of piece hashes in it. Byte strings stay bytes: the piece hashes are not
  valid UTF-8 at all, and a reader that decoded them would fail every piece it later verified. The
  reader records the byte range of the **top-level** `info` and nothing else, because the info hash
  is SHA-1 over those bytes as they arrived — and re-encoding the whole file reproduces it byte for
  byte. Malformed input is refused with the offset it went wrong at, the end of the input included,
  which is where a truncated download stops being a torrent. `S5-02` reads what is inside it.
- `S5-02` `TorrentMetadata` and `Magnet`, against two real torrents: Ubuntu's single-file image and
  the Internet Archive's twenty-three-file scan. A multi-file torrent is one byte stream laid across
  files, so a piece pays no attention to where one ends — the first piece of the Archive one covers
  the end of a thumbnail and the start of a nine-megabyte scan, and `Slice` answers both runs. The
  last piece is short, the info hash is over the raw info bytes, and a magnet's hash is the same
  forty characters whether it was written in hex or base32. `S5-03` starts the engine itself.
- `S5-03` `ITorrentEngine` now has the shape `docs/06-torrent-client.md` gives it, `BittorrentEngine`
  in the shell implements it, and `ListenSockets` in the protocol assembly binds the one port for TCP
  and UDP together. Started once and stopped once whatever ticks in between; disposing twice is safe;
  a port it cannot have is reported **with its number** and the client carries on, because taking the
  plugin down over a port costs the owner everything else it does. A magnet just taken on is
  `FetchingMetadata` and not a shade of downloading. Nothing hands it work yet — that is `S6-01`.
  `S5-04` announces to the trackers.
- `S5-04` HTTP and UDP announces, against what two real trackers really answered — the Internet
  Archive's over HTTP and opentrackr's over UDP, both captured by `tools/Capture`. An info hash goes
  into a query percent-encoded **a byte at a time**: the first version put it through a text encoder,
  every byte above 0x7F became two, and the tracker answered "not authorized" for a torrent it was
  serving. That refusal is a fixture now. A connection id is kept for the minute BEP 15 allows,
  retries are `15 * 2^n` up to eight, every tracker is announced to at once, and one that will not
  answer costs only itself. `S5-05` speaks to the peers they name.
- `S5-05` The handshake, the messages and the reassembly. A real peer's handshake is captured —
  Transmission 4.1.3 in the Ubuntu swarm — and it proves the reserved bits are read at the offsets a
  real client writes them: the extension bit on byte five, the DHT bit on byte seven. A peer
  answering with another torrent's hash is another torrent and is dropped. Messages are reassembled
  across arbitrary reads, because TCP is a stream: a bitfield arrives in several and two small
  messages arrive in one. A keep-alive is four bytes of nought and not a fault; a block nobody asked
  for is refused, length included. `S5-06` verifies pieces and writes them.
- `S5-06` Rarest-first, with the first four picked at random so something is verified early, and the
  last few asked of everybody at once. A piece is verified against the twenty bytes the torrent named
  **before** anything of it reaches the disk; a piece that fails is discarded whole, because there is
  no telling which block was bad, and a peer present at two failures is banned for the session. On
  disk a piece is written across as many files as it covers, at each one's own offset — the real
  Archive torrent's first piece covers the end of a thumbnail and the start of a nine-megabyte scan.
  Files are made at full size and marked sparse, so a six-gigabyte torrent does not cost six
  gigabytes before a peer has answered. `S5-07` fetches metadata from peers.
- `S5-07` A magnet is a hash and a name; everything a download needs is in the info dictionary, and
  the only place to get it is a peer that already has it. BEP 10's handshake settles what to call the
  message — the id is the peer's own choice and differs per peer — and BEP 9 carries the dictionary
  in sixteen-kibibyte pieces, the raw bytes following the bencoded header because bencode has nowhere
  to put them. The whole thing is hashed once against the magnet's hash: there is no per-piece hash,
  so a fetch that fails drops every peer that contributed and starts again from nothing. A magnet
  nobody will serve the metadata for is failed after `MetadataTimeoutMinutes` with the reason, said
  once rather than once a tick. `S5-08` is encryption.
- `S5-08` MSE/PE, which is obfuscation and not security: Diffie-Hellman over the 768-bit prime every
  client uses, then RC4 with the first 1024 bytes of keystream thrown away, a different key each
  way. The info hash never goes on the wire — which torrent is said as a hash exclusive-ored with
  one derived from the shared secret, so only a peer that did the exchange *and* already has the
  hash can tell. Both methods are offered always, encryption is tried first and a peer that answers
  with a plaintext handshake is dialled again in the clear. Allowed, never required.
  `S5-09` is the DHT.
- `S5-09` Kademlia over UDP, against real packets: a node id and an info hash live in the same space,
  distance is exclusive-or, and "who is nearest this torrent" is the same question as "who is nearest
  this id". A bucket per shared prefix bit, eight to a bucket, and a full bucket keeps what it has —
  the eight in it have answered and the newcomer has not. A search walks towards the hash until
  nobody can name anybody nearer, asking nobody twice. The table and **this client's own id** are
  persisted, because an id that changes on restart is a stranger to every table that knew it. A
  private torrent sends **not one packet**. `S5-10` is peer exchange and local discovery.
- `S5-10` Two ways peers arrive without anybody being asked. `ut_pex` sends **differences** — who
  joined and who left since that peer was last told — which is why what was sent is remembered per
  peer and why the once-a-minute limit is per peer too. Local discovery is a multicast packet shaped
  like HTTP and parsed by nobody's web server, at one hop so it never leaves this network, with a
  cookie so a client does not connect to itself. Both are refused outright for a private torrent, in
  both directions: a peer list arriving is the same leak as one leaving. `S5-11` is rate limits,
  choking and seeding.
- `S5-11` Token buckets that start **empty** — one that started full would take a second's worth the
  instant the plugin came up, which is when the owner is most likely to be watching something — and
  hold at most a second's worth, so an hour idle is not an hour of allowance. The lower of the global
  and the per-torrent limit decides and both are charged for what really went. Choking is tit for
  tat: the four interested peers sending the most, worked out every ten seconds, plus one at random
  every thirty so a peer that has never been given anything can prove itself. While seeding the
  ranking is by upload rate, or the choice would be between peers all at nought. Seeding stops at the
  ratio or the hours, whichever comes first, and **never early for a private torrent**. A passkey and
  an API key are swept for across every page, every prop value, the journal and the log.
  `S5-13` joins the parts up.
- `S5-13` **One instance of this client downloads a whole torrent from another**, over a real TCP
  connection on this machine, and the file that lands is byte for byte the file that was seeded. That
  is Sprint 5's acceptance in miniature: handshake, bitfield, interested, unchoke, requests, blocks,
  SHA-1 verification, the picker and the disk, all running together for the first time. A peer that
  answers with rubbish of the right length has **none of it written**, which is why a piece is
  verified before it reaches the disk rather than after. Sprint 6 is the grab and staging.
- `S6-01` A grab checks there is room **first** — a torrent that fills the disk takes the media
  server with it, since the same disk holds the library and the database — and a refusal names how
  much was needed and how much there is, because "not enough space" tells the owner nothing they can
  act on. Every tracker anybody named travels with it. **B2**: nothing that goes wrong here counts as
  a search attempt, because a client that would not take a magnet is not the episode's fault. The
  store keeps the magnet, so a torrent the client has forgotten is re-added rather than downloaded
  again, and keeps every episode a grab covers, so a season pack that fails puts all of them back to
  missing at once — blacklisted by hash, in one transaction, which is where a metadata timeout and a
  stall both arrive. `S6-02` stages what finished.
- `S6-02` **Only video files are written into a library folder**, and the check is the extension —
  a scene release ships a `.rar` the size of the episode, which no sample rule would catch. The
  largest video is the episode; a sample is never it. Size only says "sample" when the torrent holds
  something bigger for it to be a sample of, so a twenty-minute anime at a low bitrate is not
  refused. A pack is matched by the episode number in each file's own name, never by order, and an
  episode no file answered for is said to be missing rather than quietly counted as arrived. The move
  is a copy, a length check and only then a delete, so an unwritable intake folder costs nothing:
  the download is exactly where it was. `S6-03` dispatches the encode.
- `S6-03` The encoder is reached **by name through `IServiceProvider`**, never by reference, or it
  and the entity model would become part of this plugin's ABI. Every trap in
  `docs/09-host-contract.md` is now a test: the ambiguous `ILibraryRepository` spelled in full, the
  full `GetLibraryByIdAsync` rather than the folderless Lite one, everything resolved **inside a
  scope**, the id taken from the server rather than the filename and **nothing dispatched when
  nothing matched**, the show's own library, and the *first* folder with no preference. Nothing in
  the path throws: it once unwound the whole transfers cadence, so one type mismatch stopped every
  download in flight from being looked at. `S6-04` is the Downloads page.
- `S6-04` **G4**: the Downloads page is built from the **grabs** and the transfer is what may be
  missing, so a grab the client has not taken up — or has quietly lost — is a row that says which of
  the two it is, rather than being on no page at all while the episode shows as unavailable. Every
  number is real or says it is not known: peers, seeds and ratio show a dash rather than nought,
  because "0 peers" is a torrent nobody is sharing and this one has not been asked yet. A size of
  nought gets no percentage either — dividing by it prints something that is not a number. History
  carries the reason on every kind of line, since "skipped" and "failed" without one are exactly the
  entries the page is opened for.
- `S8-01` The Settings page says in plain words when neither UPnP nor NAT-PMP would map the port and
  which numbers need forwarding by hand, with every refusal the router gave. The Skipped page carries
  every refusal with the reason it was refused for and the control to overrule it; the Sources page
  says per source when it was last asked, how many rows it answered with, how long it took, its
  refusal in the site's own words and when it is next askable. All three render and none of them was
  on a route, which is where `S8-02` starts.
- `S8-02` (part one) The four pages are reachable. History reads every column rather than the two its
  first caller wanted, so a line says when and about which episode as well as why. A refusal is
  written to the history as it is refused, because the Skipped page is opened the morning after and a
  list held for the cycle would be gone by then. `source_reports` had been in the schema since the
  first migration and nothing had ever written a row to it: every ask now writes down what the site
  answered, from the harvest as well as from find, or a feed would read as never asked however often
  it ran. Next-askable is the gate's **current** interval, which a refusal has widened. The allow
  control on the Skipped page was passing "Allow" where the transport goes.
- `S8-02` (part two) Sprint 6 joined up. The search cycle hands over through `Grab` — the room check
  it went round — and writes down what it decided: the hash, the magnet a lost torrent is re-added
  from, and every episode a pack answers for. It reads the real blacklist now. The transfers cadence
  is the loop the plan had no slice for: re-add what the client lost, stop what the plugin has no
  record of and keep its files, stage what finished and ask for its encode, and blacklist what the
  client gave up on while returning its episodes to missing. **F4** is the third of those. Nothing in
  the tick throws out of itself: it once unwound the whole cadence, so one type mismatch in the
  encoder stopped every download in flight from being looked at. `DiskSpace` matches the volume
  against the ones this machine really has — handed a UNC path, `DriveInfo` answers with the free
  space of the current drive, which is the one answer that fills a disk.
- `S5-14` **This client downloads, through the port.** `BittorrentEngine` — the only implementation
  the plugin calls — parsed a magnet, wrote down the hash and stopped, for a whole sprint, and
  everything above it was correct against a client that never finished anything. It now holds a
  `TorrentRun` per torrent: announcing to every tracker at the interval those trackers asked for,
  dialling the peers they name once each, fetching the metadata under the id that peer chose,
  checking the whole against the info hash before believing any of it, opening the disk, starting
  from what the resume file says was verified, and answering whoever dials in — encrypted or in the
  clear, told apart from the first byte. Rates are measured between the last two readings and never
  averaged over the transfer. The acceptance is two engines over a real socket: the bytes that land
  are the bytes that were seeded. Three faults were found on the way and are under **Decisions**: the
  resume file could never be believed, the ephemeral port was never safe, and the client had nothing
  behind its listening socket. `S8-02` part three is next.
- `S8-02` **Every action does what it says, and every one is a control as well as an endpoint.** Run
  and Stop had answered "not-ready" since `S0-05`. The cycle runs on the plugin's own lifetime and
  the endpoint answers that one has begun — **F1**, proved with a request token cancelled before the
  request was made — and two cycles at once are one. Pause, resume and cancel reach the client, and a
  hash it is not holding is refused by name. Cancel does three things or the episode is lost, and
  does **not** blacklist: the owner said no to this download, not to this release for ever. A torrent
  added by hand is written down like any other grab and answers for no episode. `AllowRelease`
  records what the release had been refused for, and allowing something nothing refused is refused
  itself. Where each control sits follows from the component set — a table cell holds a value and not
  a button — and that reasoning went into `docs/08-ui.md`, which had said only that a control must
  exist.
- `S8-03` **A check that cannot fail is one nobody acts on**, so the health tool exits non-zero when
  anything is flagged. It also keeps `health/baseline.json`, and a source answering with **fewer rows
  than last time** is flagged though it answered: nought rows off a page covered in releases is a
  broken reader and says so loudly, and three rows where there were forty is the same fault with the
  volume turned down. Judged against the last run, never a figure written down by hand — what a
  search returns depends on the term and the day. Only what really answered goes into the baseline:
  writing a broken reader's nought down would set the bar at nought and the rule would never fire for
  that source again. Found by a mutation surviving, because the first test used a source with no row
  count at all and never exercised the rule.
- `S8-05` **The plugin could not load, and the reason was never in its code.** A class library's
  build does not copy the packages it depends on into its output, so the deployed folder held three
  assemblies against a manifest naming twelve. The host resolves a plugin's dependencies from beside
  the plugin, found none, and reported a `ReflectionTypeLoadException` — down a path that returns
  without registering the plugin, so it was absent from the server's list with nothing to say why.
  `EnableDynamicLoading` fixes it. The deploy script now ships whatever the build produced rather
  than a list of names, and the tests that guarded that list were replaced: they asserted the list
  said what it said, and the list was the fault every time.
- `S8-05` **0.4.0 is on the server, and a first install could never have worked.** The owner stopped
  the media server; all six files went over with every hash matching. The plugin had no folder on
  that machine and the script never made one, so every copy failed one at a time with "No such file
  or directory" — which reads exactly like a path being wrong. The script now asks the far side
  where plugins live and makes the folder before it copies anything.
- `S8-05` **Trackers are learned and `v0.4.0` is tagged.** The default list is no longer something
  nobody chose: every tracker the plugin comes across is kept, deduplicated, and attached to every
  grab. The one address it will never keep is one carrying a passkey — this list goes out with every
  grab, so the owner's own private tracker must never enter it, and the rule refuses the *shape* of a
  secret rather than looking for the word, which is what makes it hold for a key nobody has thought
  of yet.
- `S8-05` (part) **The compiled assembly said 1.0.0 for the whole of 0.4.0's development.** The
  manifest and `PluginIdentity` both said 0.4.0 and a test held those two together; the third copy
  was left to the compiler and nothing looked at it. It matters at exactly one moment, and it is the
  one nobody can afford to get wrong: a deploy onto a server that was not stopped fails and leaves
  the old build in place, which looks exactly like a deploy that worked, and somebody checking the
  file's version to tell the two apart would have been reading a number that never changed. The
  version is set once in `Directory.Build.props` now and the test holds all three.
- `S7-03` **The join `S1-03` and `S3-03` never had.** One builds an episode's absolute number from
  the library's own episode list; the other asks the name pool under that number. Both were proved on
  their own and nothing ever said whether the number the library produces is the number a release is
  really posted under — which is the whole of anime support: a fansub row carries no season tag at
  all, so a number out by one finds nothing at all while every page still reads as though the plugin
  were working. A seeded anime library now runs through the derivation and the whole chain to a
  decision against the captured Nyaa page, and an absolute out by one fails it. The Shows page's
  media type was already done in `S1-04`. The real dry run over the real anime library is the
  owner's.
- `S7-02` **A source can say which libraries it is worth asking about.** `docs/05-sources.md` had
  scoped Nyaa to *indexer (anime)* since Sprint 2 and nothing in the catalogue could express it, so
  every television search asked it — a paced request per episode spent on a site carrying almost no
  television, taken from the sources that would have answered. A source that names no library is
  asked about all of them, so the field switches nothing off by omission. The other half of the
  slice, "ranked first for an anime show", was a **conflict between two specs**: the table gave Nyaa
  priority 30 and The Pirate Bay 45. The catalogue is the thing that was wrong — for anime Nyaa is
  often the only site with the release — so it is 50 now and `docs/05-sources.md` says why. Steps 1
  and 3 of this slice were already done in `S3-03` and `S4-01`.
- `S7-01` **Most of this slice had already been done by `S3-01`**, which parsed every name on every
  captured page and took five more captures when the anime cases turned out to be on none of the old
  ones. What was actually missing was one word: `Complete` was implemented as a pack marker and had
  no test, so deleting it broke nothing. It has one now, over the two rows on the Nyaa capture that
  really carry it. One rule still has no capture to test it against and is written down rather than
  faked — the pack word only counts when nothing says which single episode it is, and no captured
  row anywhere has both a single episode and a pack word.
- `S7-01` **Two flakes of my own making, found by running the suite four times rather than once.**
  `SqliteConnection.ClearAllPools()` is process-wide and I had spread it across four more test
  classes in `S8-02` and `S8-04`; xunit runs classes in parallel, so one class clearing the pools
  disposed a connection another was reading from, and it surfaced as
  `ObjectDisposedException: SQLitePCL.sqlite3` in a storage test that had nothing to do with it.
  Nothing clears pools now: a temporary folder that will not delete is left alone, which is what a
  temporary folder is for. And a hardening test asserted its failure was the **only** one in the
  journal, so it failed whenever another test held the listen port — asserted by name now, not by
  count. **A suite is judged over several runs, not one.**
- `S8-04` (part) The deploy script shipped **no `sources.json`**. It is built beside the assembly on
  purpose — **C1**, the catalogue is read from the assembly's own folder — and the deploy list did
  not have it, so every deploy left the plugin reading yesterday's sources or, on a fresh install,
  none at all: seventeen sources become nothing, and it asks nobody anything while looking perfectly
  healthy. The list is now checked against the projects the solution really builds, which is the
  fault it already had once with the protocol assembly. The script also refuses to copy anything
  while the server is still running, rather than leaving the hash check at the end to explain it one
  file at a time. `OneAtATime` is **F3** with a name and three tests. And a cycle that throws is
  journaled rather than swallowed: a run started from the button is a task nobody awaits, so an
  exception there went nowhere at all.
- `S5-12` Resume is a **cache and is treated as one**: a file whose size or modification time has
  changed takes every piece covering it back to unverified, including the pieces it shares with the
  file either side — asserted against the real Archive torrent, where the largest file shares its
  last piece with its neighbour. It is written every interval and on a clean stop, to a temporary
  name and moved into place. A stall is no progress **and** no peers for the whole limit, with the
  clock starting at the first reading rather than the second, or a restart quietly buys a dead
  torrent one more interval every time. Pause keeps the verified pieces. Recovery sorts every torrent
  into add, stop, stage or carry — **F4** is the stage pile — and a magnet with no metadata is not
  mistaken for a finished torrent, which is the trap in comparing two numbers that can both be
  nought. Ports are UPnP then NAT-PMP, with every refusal kept for the Settings page.

## Decisions

Anything decided that the specs did not already say. If a decision contradicts a spec, fix the spec
and note it here.

- **A start settles once, whichever cadence ticks first — the flag moved rather than went.**
  S10-04 as written said the first-tick flag "goes, and with it the special case that made a start
  different from a tick". Carried out literally that would have deleted a fix rather than moved it:
  the flag exists because what the library holds is derived rather than stored, so a restart that
  waited for the six-hourly cycle carried whatever the last run left behind — on 24 August 2026,
  shows a broken build had put there that the owner does not have. What was wrong was never that a
  start is special; it was that one tick of one cadence was. So the start runs the maintenance work
  once, in `ExecuteAsync`, whichever cadence ticks first, and no cadence has a first pass unlike its
  others. If that first tick happens to be maintenance, the housekeeping runs twice that once; every
  part of it is idempotent, and a special case to save the second pass would be the special case
  this removed. The slice and `docs/01-plugin.md` both say this now.
- **A show is the owner's when it has an episode on disk, and only then.** Reversed on 24 August
  2026, the same day it was written the other way round. Taking every show a library holds put the
  plugin on 479 grabs in one afternoon — Family Guy alone claimed 456 episodes nobody had asked
  for. A row in a library is not a show the owner added; one episode on disk is the only thing that
  says so, and it is the same rule the server's own card query uses. `docs/02-library.md` is
  corrected back. A show newly added with nothing on disk is genuinely invisible to a plugin, which
  is a gap in the host contract and is media-server issue #34, not something to work around here.
- **The has-a-file rule is a workaround, and it goes when media-server #36 lands.** A show is the
  owner's when it has an episode on disk, because nothing else distinguishes one. The reason is
  media-server #36: identification imported a whole show into the library on a guess about a
  filename, before confirming anything, and again on four alternatives it was unsure about — 13
  shows and 1,604 episode rows on the owner's server, The Simpsons at 887 and Family Guy at 483.
  Those 12 were detached by hand on 25 August 2026, X-Men '97 kept because the owner added it.
  Once #36 removes those two call sites, only the add-show button and a file on disk can attach a
  show, so **membership of a library becomes the rule** and this plugin should use it: it is right
  on the day a show is added, which is the day it matters. Do not make that change before #36 is
  in — the library looking tidy today is not the same as the cause being gone.
- **An episode's staged name comes from the episode, never from the release.** The owner's rule,
  24 August 2026: `Sugar.2024.S02E02.1080p.mkv` — show, year, number, quality, and nothing else.
  Two releases of one episode at one quality therefore come to one path, so a second copy cannot
  exist rather than merely being tidied away afterwards. The name is decided at the grab, not on
  the disk the torrent client writes to: that client is proven and is not to be touched.
- **The intake folder holds what is needed and nothing else.** The owner's rule, 24 August 2026.
  Anything a grab is waiting on stays; everything else is cleared, folders included, and each
  deletion is written to the log. This reverses "this plugin does not delete what it did not make":
  the folder only ever grew, to twenty-two entries for five episodes, all of them read on every
  tick.
- **The episode's own id is read from the server's database, past the contract.** `EncodeDispatch`
  resolves `NoMercy.Database.MediaContext` by name and asks `Episodes` for the row matching the
  show, season and number the plugin itself chose. The contract carries no episode id, and without
  one the encode job looks up media 0, registers nothing, and leaves the library empty while the
  queue counter moves. Deliberate and temporary: media-server issue #35 adds `PluginLibraryEpisode.Id`,
  and #30 as amended moves the folder decision server-side. Between them the five server types
  `EncodeDispatch` names by hand all go, and that file keeps no reflection.
- **Duplicate grab rows are cleared on every start, not once as a migration.** A migration clears
  what existed when it ran. The owner's ran, and seven pairs made later that same day were still
  there afterwards.
- **A grab is done when the library has the episode, not when the file is copied.** Staged and
  dispatched are states of their own, so a refused encode is asked for again rather than forgotten,
  and the copies are deleted only once the encode has landed.
- **The contract is packed from the released server.** `dev` carries a fixed `0.1.404`, so packing
  from it produced a package NuGet believed it already had. This repository sat on it while the
  server shipped `0.1.478`.

- **Nothing is uploaded on a public torrent, and `docs/06-torrent-client.md` is corrected.** The
  owner's rule, given on 22 August 2026 when they found their own client at a ratio of 0.17 on a
  public swarm. The spec had said to seed everything to a ratio of 1.0 or 48 hours. A peer on a
  public torrent is never unchoked and never served; only `info.private` uploads. It costs download
  speed, because a swarm reciprocates — that is the trade the owner chose.
- **There is no choking round, and the spec is corrected.** It follows from the rule above: a public
  torrent has nothing to choke and a private one wants every peer unchoked. `ChokeCycle` was written
  and tested in Sprint 6, wired to nothing, and is now deleted rather than connected.
- **Only video files are downloaded, not merely staged.** The whitelist ran after the whole torrent
  had arrived, so a 1.2 GB executable named after an episode downloaded to completion on
  22 August 2026. It now decides which pieces are ever asked for, and a torrent with no video in it
  is refused without a byte being fetched.

- **A3 is wrong and `docs/10-known-failures.md` is corrected.** It said an indexer is asked the full
  release name and nothing else. Measured against the sites on 22 August 2026, apibay answers
  `Silo S03E08 1080p WEB H264 CAKES` with "No results returned" and `Silo S03E08` with twelve rows,
  the first seeded by six thousand; EZTV's box is labelled *Search title* and answers a release name
  with nothing. Four of the eight indexers asked that cycle read nothing off a release every one of
  them was carrying. The failure A3 recorded was real and its cause was named wrongly: 0.3.4's fault
  was having no rule that a row must be a release of the episode asked about, not the breadth of the
  question. That rule is `ReleaseFilter.IsFor`, and with it the question can be as broad as the site
  needs.
- **A search answers for every gap of the cycle it fits, not only the one it was made for.** A site
  asked about one episode answers with the whole programme. Four 1080p copies of Silo S03E04 to
  S03E07 — every one an episode the library was missing — came back from a search for S03E08 and were
  discarded, each recorded as refused for not being S03E08. They are now offered to the gap they
  answer for, and a row for another episode is not a refusal at all: nobody offered it for this one.
- **The best copy is tried, and then the next one.** The ranking is walked until a copy can actually
  be had. TorrentBay outranks everything on seeders and names its torrents only to a signed request;
  while that request was unwritten the cycle chose its copy, followed it, found nothing and stopped.
  A copy the client refuses settles nothing either.
- **KickassTorrents is removed**, on the owner's decision of 22 August 2026, host and all. Asked a
  full release name that day it answered with no listing and one magnet anywhere on the page, and
  that magnet was a wallpaper pack. **E4's prescribed fallback — no rows, so take a magnet anywhere on
  the page — is unsafe and is corrected with it:** a row is named by the torrent it points at, never
  by the page around it.
- **TorrentBay's signed POST is written**, and the site was seen to answer it: asked for Silo S03E08
  on 22 August 2026 it named `99B54F771D311003FF6B6F95F8D54FCACC6DC08C`. `docs/05-sources.md` carries
  the whole request. A source can now declare the parameter that asks for its next page and how many
  are worth reading; TorrentBay declares three.
- **A search attempt is counted, at last.** Nothing counted one, so `attempts` stayed at nought on
  every row of the owner's library: `MaxSearchAttempts` decided nothing, no episode ever reached
  *given up for now*, and the queue — ordered by last-search — ran in the same order every cycle.
  **B2** decides what counts: only an episode an indexer was really asked about.
- **The owner's own tool is the reference for what a good decision is**, and they pointed at it twice
  before I read it: `github.com/Fill84/BeastStack/tree/main/torrent-feed`. Three of its rules are now
  this plugin's, each because a real decision here was wrong without them. Its resolver ranks *exact
  scene release first, then the site, then most seeders*. Its `matches()` refuses any foreign-audio
  marker even beside an English one, `MULTi` and `DUAL` included. Its `name_matches()` accepts a show
  name in exactly two places — leading the title with only a year or a country after it, or ending
  where the episode marker begins, which is where a franchise prefix leaves it (*Special Ops
  Lioness*). Its comment on the country list names the fault the shortness is guarding, and it is the
  same one seen here: a loose list reopens *Lucky* matching five other programmes.
- **The codec and the resolution are chosen from a list.** A box takes anything, and a codec spelled
  a way the parser never answers refuses every release there is with no reason the owner can read. A
  blank codec wants nothing, exactly as `any` does — the field was empty on the server for a while,
  and an empty string is a codec no release claims.
- The plugin keeps id `1SBQT26FHF98EBRPYVRGD92CZF`, so 0.4.0 is an upgrade of the installed plugin
  rather than a second one.
- The BitTorrent protocol is written in this repository. No third-party torrent library.
- The challenge solver is the plugin's own Chrome on a hidden desktop.
- There is no follow list. Every show in every `tv` and `anime` library is in scope, and every aired
  episode without a file is fetched, however old.
- Show status is not used and not needed: an ended show is exactly the kind with gaps to fill.
- **No shipped indexer publishes a magnet on its listing** — not one of the nine captured. Every one
  of them carries the row's own page instead, so following a detail page to its magnet is not a
  TorrentBay speciality but the ordinary route.
- **TorrentGalaxy's rows are `tgxtablerow` divs, not table rows**, and the page holds seven distinct
  forty-hex strings with no magnet anywhere — which is **E6** exactly. Its title is on the anchor's
  `title` attribute.
- **UDP chooses the port, then TCP is bound to the same number.** Windows reserves ranges for
  Hyper-V and WSL and refuses them for UDP while handing the same numbers out for TCP, so asking TCP
  first gives a number UDP often cannot have — measured, on this machine, as a test that failed with
  a permission error rather than an in-use one. The refusal now says which of the two it was.
- **A test that needs a port holds it rather than sampling one and letting go.** A port this process
  released is one another test can take between the two lines, and that was a real flake here.
- **The mutation harness must touch a file after restoring it.** `shutil.move` keeps the original
  timestamp, so the restored source is *older* than the assembly built from the mutated one and
  MSBuild sees nothing to do — the suite then fails on code that is already correct. Three failures
  in this sprint were that and nothing else. The scripts now set the modification time on restore;
  when in doubt, `rm -rf bin obj` for that project and run again.
- **The intermittent full-suite failure was real, and it is fixed** (19 August 2026). It was written
  down during `S5-07` as a run that would not reproduce. Run with `--logger "trx"` it named itself at
  once: every failure was in `ListenSockets`, and the message read "Port 0 is one this machine will
  not allow". UDP picks an ephemeral number and TCP has to have that same one, and the number UDP is
  given is not always one TCP can have — **measured at one attempt in seventy-five** here, some
  already in use and some refused outright. Windows reserves whole ranges, so a run of eight
  consecutive attempts was refused on 50379 to 50387: retrying inside a block that wide never escapes
  it. Making the two protocols take turns choosing was the first fix and it was not enough: five tests
  went down together on 59435 to 59451 the same day. **The machine was then asked what it reserves**
  — `netsh interface ipv4 show excludedportrange` — and the answer settles it. The dynamic range is
  49152 to 65535; 1460 of those ports are excluded in fifteen blocks a hundred wide; the TCP set and
  the UDP set are **not the same**, which is exactly why a number handed out for one is refused for
  the other; and **nothing below 49152 is excluded at all**. The pool walks forward, so once it is
  inside a block every consecutive attempt fails together. A request for any port now **draws its own
  number between 20000 and 48000**, eight independent draws, and never asks the operating system to
  choose — which makes the behaviour better and not only quieter. A port the owner named is still
  asked for once and refused by its number. The exception named the number that was asked for rather
  than the one that failed, so a request for any port reported "Port 0", which is the one thing
  nobody can act on.
- **The plugin deadlocked on the first cadence tick after every restart** (19 August 2026).
  `ChainAsync` held the migration semaphore and then asked for the database to build the name pool —
  and migrating takes that same semaphore. `SemaphoreSlim` is not reentrant, so the plugin waited on
  itself. For ever. There is no exception and no log line: the tick simply never returns, and because
  no cadence may overlap itself, that cadence never runs again for as long as the server is up. A
  plugin that has quietly stopped looks exactly like one with nothing to do.
  **It had been there since the chain existed** and no test ever built one: every test that ticked a
  cadence used an unconfigured plugin, which returns before the chain. The ledger added in `S8-02`
  made it a second time over, which is what finally made somebody look.
  Everything the database is needed for is now asked for **before** the lock is taken. The regression
  test races the tick against a clock rather than awaiting it, because a test for a deadlock that
  deadlocks is a suite that never finishes.
  It also cost an hour of chasing the wrong thing: `dotnet test` reports a hung host as
  "Test host process crashed", and a `timeout` around it turns a hang into what reads exactly like a
  crash. **A crash with no stack and no exception is a hang until proved otherwise** — the host's own
  `--diag` log, five minutes of idle polling, is what said so.
- **Resume could never be believed, and every restart verified the whole torrent** (19 August 2026).
  The resume file records a modification time in whole seconds — deliberately, because that is the
  same number on any machine it is moved to — and `Trust` compared it to the live one exactly. A real
  file's timestamp carries a fraction of a second, so every file looked touched the moment the resume
  had been written and read back, every piece went to unverified, and a six-gigabyte torrent was
  re-hashed on every start. It is exactly what `S5-12` was written to prevent. It survived because
  every test of `Trust` judged a `ResumeData` built in memory that had never been through `Write`.
  The comparison is to the second now, which is all the file keeps and finer than any change worth
  noticing: nothing rewrites a file and leaves its length alone inside the same second.
- **`BittorrentEngine` does not download, and no slice was ever written for the part that would**
  (19 August 2026). It parses a magnet, records the hash and the trackers, and stops: nothing
  announces to a tracker, dials a peer, fetches metadata, opens a disk or writes a byte, and
  `FilesAsync` answers with nothing at all. `S5-13` proved `TorrentSession` downloads a whole torrent
  from a second instance of this client over a real socket, and that is true — but the session is
  never joined to the engine, and the engine is the only thing the plugin ever calls. It is now
  `S5-14`. Everything built on top of the port is correct against a client that never finishes
  anything, which is why nothing noticed.
- **A bitfield is high bit first**: the top bit of the first byte is piece nought, which is the
  opposite of what a bit index usually means. A client that got it backwards would ask every peer for
  what they do not have and refuse what they do.
- **`EndgamePieces` is eight and no document gives a number.** It is the point at which the last
  pieces are asked of every peer at once; the tail of a download is otherwise spent waiting on the
  slowest peer holding the last one. Written as a documented constant in the picker.
- **The Settings page renders no private-tracker rows yet, because nothing configures one.** The
  view draws them from `settings.PrivateTrackers` and does it correctly — the passkey field says the
  secret is set and the announce address shows `{passkey}` where it goes, both asserted in
  `SecretsNeverEscapeTests`. What is missing is the actions that add one, which `docs/08-ui.md` names
  as `AddPrivateTracker` and which `S8-02` owns. Nothing is wrong; it is simply not reachable from
  the page yet.
- **The encode dispatch is tested by being the server.** `tests/.../Hosting/FakeServer.cs` declares
  types under the exact namespaces `docs/09-host-contract.md` names, so the plugin's by-name
  resolution finds them. It is the only way this path can be tested at all without referencing the
  real encoder — which is the thing the contract exists to prevent — and it means the Lite variant,
  the three-argument file-list overload and the unregistered `ILibraryRepository` are all present to
  be wrongly chosen.
- **A sample filter was hiding the video filter.** Every non-video in the staging test was small
  enough to be taken for a sample, so removing the extension check entirely still passed — a mutation
  found it. The test now carries a three-gigabyte `.rar`, which is what a scene release really ships,
  and the rule that matters is the extension.
- **"Smaller than fifty megabytes" was condemning legitimate episodes.** A twenty-minute anime at a
  low bitrate is smaller than that and is the whole torrent. Size now only says "sample" when there
  is something bigger in the same torrent for it to be a sample of.
- **A size was being formatted in the machine's culture.** "needs 3,7 GB" on a server set to Dutch,
  which is this one. Every number a person reads is now written in the invariant culture: a figure
  whose meaning depends on where the machine was set up is one nobody can quote back reliably. Found
  by a test that expected "3.7 GB" and got the comma.
- **`COLLATE NOCASE` in the grab store was defending against nothing.** Every hash is upper-cased on
  the way in and on the way to a query, so matching is exact; a mutation removing the collation
  survived every test, because nothing this code writes could ever need it. The normalisation is the
  rule, it is tested, and the collation is gone.
- **The plugin's dependencies ship with it, and the deploy is derived rather than listed**
  (21 August 2026). `EnableDynamicLoading` makes the class library copy its packages into its own
  output, which is what a plugin loaded out of its own folder needs; without it the host's resolver
  finds nothing beside the assembly. The script ships everything the build produced, minus symbols
  and documentation, plus native code for the one platform being deployed to — SQLite ships built
  for twenty and they are 33MB of a 41MB output, all of it travelling base64 through ssh.
  **What this cost is worth recording.** Three tests covered the old hand-written file list and all
  three passed through every one of its faults, because what they asserted was that the list
  contained the names it contained. `docs/10-known-failures.md` § H is about exactly this shape of
  test and it was written here anyway.
- **The deploy script builds the remote path on the far side, not here** (21 August 2026). It used to
  send `$LOCALAPPDATA` through unexpanded and glue POSIX separators onto it, which on a Windows host
  gives `C:\Users\...\Local/NoMercy/plugins/...` — a mixture no redirect can be trusted with. It now
  asks the server for `cygpath -u "$LOCALAPPDATA"` once and uses the answer, and it makes the plugin
  folder before copying. Both faults only show on a server that has never had this plugin, which is
  why the whole of 0.4.0's development never met them.
- **`DefaultTrackers` is learned, not chosen** (owner, 20 August 2026 — this replaces the decision of
  18 August, which was to ship it empty and attach only what a source's own magnet supplied). Every
  tracker the plugin comes across is kept without duplicates and travels with every grab: more
  trackers is a faster download, and the swarm one release was posted to is usually the swarm the
  next one is in.
  **A passkey is never kept.** A private tracker's announce address carries the owner's own key, and
  this list goes out with every grab — so learning one would hand their credentials to every public
  swarm they download from and print them on the Settings page. Anything with a query string or user
  information is refused, because that is where a key lives and no public tracker needs either, and
  so is anything on a host they configured as a private tracker.
  **Nothing is learned yet in practice, and that is a fact about the captures rather than the code.**
  No shipped listing publishes a magnet — all nine captured carry the row's own page instead — so
  trackers appear only once a chosen copy has been followed to its page. The one captured detail page
  that does publish a magnet with trackers belongs to a source whose rows this profile refuses, so no
  test drives the collection end to end; `TrackerBook` carries all the judgement and is tested twelve
  ways. The next capture run should take a detail page for a source the profile accepts.
- **A port that cannot be mapped must tell the owner to forward it by hand** (owner, 18 August 2026):
  try UPnP, then NAT-PMP, and when both fail say plainly that TCP and UDP 51413 need forwarding to
  this machine. `PortMapping` already tries both and keeps every refusal; what is missing is
  rendering it, which is now a step in `S8-01`.
- **The end-to-end transfer found two faults that no unit test could have.** A peer's unchoke was
  being swallowed by the connection — it updated the flag and never told the session — so nothing was
  ever requested and the download sat at nought for ever. And the test itself deadlocked: the side
  that answers reads before it writes, so awaiting its handshake before the dialling side has sent
  one waits for ever. Both are in the code and the test as comments, because both are exactly what a
  real client does to itself.
- **Neither UPnP nor NAT-PMP answers on this network, and that is measured.** An SSDP search for
  `ssdp:all`, `upnp:rootdevice`, `InternetGatewayDevice:1` and `WANIPConnection:1` was answered by no
  device at all, and the gateway at 192.168.178.1 did not answer NAT-PMP either. So the port-mapping
  code is stated protocol, round-tripped, like MSE — and, more usefully, **this is why no peer has
  ever been able to dial this machine**. On this network the owner would have to forward the port by
  hand, which is exactly what the failure message on the Settings page is for.
- **`ResumeInterval` was named in `docs/06` and existed nowhere else.** It is now
  `ResumeIntervalSeconds`, default sixty, in `ClientLimits` and in `docs/04-domain.md` § Settings.
  Sixty seconds is what a crash costs in re-hashing: short enough not to matter, long enough that the
  disk is not busy writing resume files instead of the download.
- **The atomic resume write is not covered by a test, and cannot be.** The file is written under
  another name and moved into place, so the old one stays good until the new one is whole; a mutation
  that writes straight to the destination survives every test in the suite, because the difference
  only shows on a power failure between the two writes. What is asserted is what is observable: the
  file reloads, and no temporary is left behind.
- **Local discovery is the first thing in this client with a real network test that passes.** Two
  sockets on this machine, a real announce on 239.192.152.143:6771, really heard — it is in
  `tests/*.Integration` because a machine with no multicast route would fail it, and that is a fact
  about the network rather than the code. The first packet after joining a group is lost often enough
  on Windows that the test sends two.
- **The DHT is the one part of this client that could be captured for real.** A node answers a UDP
  packet from anybody, so `tools/Capture --dht` took a `ping`, a `find_node` and a `get_peers` from
  `dht.transmissionbt.com:6881`, and `--dht-peers` followed the nodes it named thirteen hops into the
  Ubuntu swarm until one answered with real peers. Two things in those answers are worth knowing:
  a **router hands out no token at all** — it holds nothing and nothing may be announced to it, so a
  client that assumed a token was always there would throw on its first answer — and a router's
  `nodes` is the **same node id at eight addresses**, which is one logical node behind a cluster.
  A real node's `nodes`, by contrast, is eight different ids that all share at least twelve leading
  bits with the hash asked about; that is Kademlia visible in captured data, and it is the property
  the walk relies on.
- **A captured DHT answer carries this machine's own public address** in BEP 42's `ip` field, and the
  anonymiser did not know about it until it nearly went into a fixture. It now replaces that too,
  alongside every `nodes` and `values` address. Anything captured from now on gets read before it is
  committed.
- **The bootstrap node list was measured, because no document named one.** On 18 August 2026
  `dht.transmissionbt.com:6881` and `dht.libtorrent.org:25401` answered a ping from here;
  `router.bittorrent.com:6881` and `router.utorrent.com:6881`, the two everybody quotes, answered
  nothing. `docs/06-torrent-client.md` § DHT now carries the list and that measurement.
- **The mutation harness counted a mutation that would not compile as "caught".** Warnings are
  errors here, so a mutation that leaves a field or a using unused fails the build — and a build
  that fails makes `dotnet test` exit non-zero, which the old script read as a test doing its job.
  One real hole was hiding behind it (below). The harness now builds and tests as two steps and says
  **NO BUILD** when the mutation proves nothing. Any sweep run before this is worth repeating; the
  `S5-07` set was, and one of its eighteen was this.
- **The info hash is taken over the raw bytes, and no test can tell.** A mutation hashing a
  re-encode of the parsed info dictionary survives every test in the suite, because this reader keeps
  every entry it did not recognise and this writer puts them back in the order they were read — for
  a real torrent the two are the same bytes. `BencodeTests` now asserts that byte-for-byte round trip
  on both real torrents, which catches a writer that changed how it spells an integer or a string. It
  cannot catch one that sorts keys, because a real torrent's keys are sorted already. The raw bytes
  stay: what a peer checks is what arrived, and a file whose keys were out of order would prove it.
- **The metadata is tested against the real info dictionary, not a made-up one.** The 484 kilobytes
  inside `ubuntu-desktop.torrent` are taken out as raw bytes, handed back a piece at a time through
  the writer and the reader, and have to reassemble to Ubuntu's own published info hash and to the
  same file list, piece length and piece count the whole-file reader gives. The framing around them
  is BEP 9's and BEP 10's, stated, for the reason in the note below.
- **`Bencode.ReadPrefix` is the one reader allowed to leave bytes behind it.** Everything else refuses
  trailing bytes, because a torrent with something appended is not a torrent. BEP 9 is the exception:
  a metadata piece is raw bytes following a bencoded dictionary, and where the dictionary ended is
  the only way to find where they start.
- **Blacklisting a failed hash and returning its episode to missing is `S6-01`'s, not `S5-07`'s.**
  Both need the grab — the only thing that knows which episodes a hash was fetched for — and nothing
  writes one until Sprint 6. The engine fails the torrent and says why; `S6-01` acts on it, for a
  metadata timeout and for a stall alike. `SPRINTS.md` has been corrected on both sides.
- **No peer on this network will send a message, so the peer-*message* tests are stated rather than
  captured.** Fifty peers were dialled over several announces by `tools/Capture --peer`: almost none
  accepted a connection at all, and the one that did shook hands and said nothing further, even after
  an `interested`. The handshake fixture is that real conversation. The message bytes are BEP 3's own
  layout written out, round-tripped through the writer and the reader, and labelled as such in the
  test file — and the capture tool is already written for the day a peer will talk.
- **This machine's outbound connections are not filtered; the peers simply will not accept one.**
  Measured: TCP to ports 80, 8080, 6881 and 51413 on a host that answers on every port all connected.
  Fifty peers out of a real Ubuntu announce, dialled with MSE by `tools/Capture --mse`, refused the
  connection every one. They are behind NAT and reachable only if they dial *us*, and nothing can
  dial us until the port is mapped, which is `S5-12`. A real peer conversation therefore needs one of
  three things: the listen port forwarded, a real client run on this machine or the LAN to dial, or
  `S5-12`'s UPnP working. Until then peer-*message* bytes stay stated rather than captured. A DHT
  node, by contrast, answers a UDP packet from anybody, so `S5-09` can capture for real.
- **A captured tracker answer has its peer addresses replaced with TEST-NET-1.** The first peer a
  tracker names is usually this machine, and the rest are strangers in a public swarm; a fixture in a
  public repository must not publish either. Everything else — the lengths, the order, the intervals,
  the counts — is exactly as it arrived, and those are what a parser can be wrong about.
- **The bencode types live in the assembly's own namespace**, not a `Bencode` one: a class and the
  namespace above it cannot share a name, and `Bencode.Read` is what every caller writes.
- **Every number asserted about the torrent fixture was read out of it by a second implementation**
  — a few lines of Python — including the info range and its SHA-1. A parser tested against numbers
  it produced itself agrees with itself and with nothing else.
- **`tools/Capture --file <address> <name>` saves raw bytes.** A `.torrent` is binary and the
  plugin's fetch answers a string; nothing else in the tool could save one.
- **An unconfigured plugin searches for nothing and says so once.** It has nowhere to put a
  download, so a cycle would spend every site's patience on a file that could only be thrown away.
  It is also what keeps the shell's tests off the network: a feed tick on a fresh install builds no
  chain and starts no browser.
- **A copy's own announced title is judged by the name rules too.** A search puts a release name to
  seventeen indexers and each answers with whatever its own search engine thought; without this a row
  for another programme is taken because it came back well seeded.
- **A season pack is a candidate for every episode of its season, and never an answer.** The harvest
  files a pack under its season and nothing else would look there, so the pack rules were
  unreachable. It is not an answer because an episode whose season has one gap would otherwise never
  be asked about again.
- **At most `MaxSearchAttempts` names are searched for per episode per cycle, and every one of them
  is searched.** Twenty spellings of one release times seventeen indexers is a cycle that gets the
  plugin banned, so the setting caps how many names are tried; it already means "how many times an
  episode is looked for", so it is that number rather than a new one.
- **Nothing is taken until every one of those names has been asked of every indexer.** The owner's
  decision, 26 August 2026, replacing the earlier rule that stopped at the first name producing a
  copy worth taking. A site only answers about the name it was asked, so an indexer holding the
  release under another spelling was asked and never found it — and its trackers never reached the
  magnet. Two Lioness episodes sat at "fetching metadata" with no peer and no seed while the same
  release seeded through trackers only a later name would have found. The cost is names times
  indexers rather than indexers, and the per-host gate is what keeps that civil: every request waits
  its turn behind that site's own pace, whoever asked for it.
- **A magnet is built from a bare info hash when a detail page prints one.** TorrentFunk's page
  carries no magnet at all — the real capture has exactly one forty-hex string on it — and a hash is
  all a magnet needs. The trackers come from whatever else knows the torrent, and from the owner's
  own list at the grab.
- **`tools/Capture` can save one particular address**, not only a search: `Capture "TorrentFunk"
  --page <url> <name>`. A detail page cannot be captured any other way, because it has to go through
  that source's own gate and clearance.
- **Two rows of the profile table have no data behind them, and both are recorded rather than
  guessed at.** *Blocked group* has no list of groups in any document, and `ExcludeTerms` does that
  work: a forbidden term is looked for in the whole name, and a group is part of the name it appears
  in. *Size within bounds* has no bounds — no setting names a minimum or a maximum — so nothing is
  checked. `docs/04-domain.md` now says so in both cases.
- **A release that does not say what resolution it is is refused**, with a reason saying exactly
  that. Nothing in the documents covers the case; it is the same choice as the codec tag and for the
  same reason — what a release does not say is where the thing nobody wanted hides.
- **A copy whose seeder count the site did not publish is not refused for having none.** Null is not
  nought, and refusing on a number nobody gave is A1's own category error wearing the other hat.
- **A show is asked with its year when its title is one word.** `docs/02-library.md` says "where a
  show's title is a common word" and defines no test for one; the four shows in the real library
  that need it — Lucky, Sugar, Lioness, Silo — are all a single word, and adding the year to every
  show doubles every request for nothing. The rule is one word, and it is in `NameResolve.Terms`.
- **The pool answers only a release whose title is spelt as the library spells the show.** The key is
  exact, as `docs/04-domain.md` § Storage schema has it, so `One Piece (Elbaf arc) - 1172` is filed
  under a key nothing looks up and that name is harvested for nothing. It costs a request, never a
  wrong download: the name databases are asked and answer. Worth fixing deliberately — the pool would
  have to be looked up by slot and the titles matched with `TitleMatcher` — and it is not worth
  inventing a scheme for inside a slice that did not ask for one.
- **The pool key runs the words of a title together; a title match keeps them apart.** Two
  normalisations, and the reason is in the captures: one Nyaa page spells one episode's show three
  ways, differing only in what became of the apostrophe, so a key that keeps the gaps files three
  names for one episode. A match cannot run them together, or *Silos* begins with *Silo*.
- **The feed cadence is not wired to the harvest yet.** It needs the fetch chain, the browser and
  the host grants assembled in one place, which is `S4-04`. Noted in `SPRINTS.md` under `S3-02` and
  in the plugin beside the cadence itself, so neither reads as an oversight.
- **Release-name parsing lives in `Core/Naming`**, where `docs/03-architecture.md` § Project layout
  puts it. `S3-01` wrote it into `Core/Domain`; moved with no other change.
- **A release name's codec is answered as a family**: `h264`, `h265`, `xvid`, `divx`. `H.264`,
  `x264`, `AVC` and a bare `264` are one codec, and a rule written against a spelling refuses six
  copies of the thing it was asked to accept.
- **Two assertions in `ReleaseNameTests` are written by hand, and they are the only ones.** No
  captured page carries a title ending in ` - ` with the resolution straight after it, nor one
  without a season tag carrying a dash against a digit — so the two halves of the separator rule in
  `docs/04-domain.md` have nothing real holding them in place. CLAUDE.md says parsers are tested
  against captures only and also that every rule needs a test that fails when the rule is deleted;
  where the two collide the rule keeps its test, said out loud in a comment beside it. Everything
  else in that file is a real name, checked against the capture it came from.
- **The captures disagreed with the field table twice, and the captures won.** An absolute number is
  written `EP1173` as often as ` - 1173`, and one Nyaa title spells the same programme *Pokémon* and
  *Pokemon*. Both are corrected in `docs/04-domain.md`: the `EP` form is read, and accents are
  folded before titles are compared.
- **The health tool counts release-shaped *names*, not links.** `docs/05-sources.md` said links; six
  of the seventeen sources answer JSON or XML with no anchor and no magnet anywhere in them, so a
  count of links would report every one of those as having nothing to offer on the day its reader
  broke. A name is release-shaped when it carries a resolution, a codec or a source — never the
  episode number, which is in the term searched for and so appears on every page that echoes the
  question back. Corrected in that document.
- **A route to a torrent counts the row's own page.** No shipped indexer publishes a magnet on its
  listing, so a health check insisting on one would flag all of them.
- **The health tool's tests live in the shell's test project.** The tool sits on top of the shell's
  fetch — the body it clears is the fetch's own — and Core can see neither.
- **A challenge that will not clear sometimes clears on the next attempt.** KickassTorrents and
  TorrentBay each refused once and answered a minute later. Retry before concluding a site is gone.
- **Three things in the captures do not match `docs/05-sources.md`, and the captures win.** EZTV's
  titles end `[eztv]`, not `[eztv.re]`. Neither the EZTV nor the KickassTorrents listing carries a
  magnet at all today, so both need the detail-page route the doc describes for 1337x. Correct that
  document while writing the readers.
- **A reader is chosen by the source's `reader` field or, failing that, by its `kind`.** Ten of the
  seventeen shipped sources name no reader at all — PreDB's kind is `rss` and that is the whole of
  how it is placed — and `Readers.For` consulted only the reader field, so every feed and every JSON
  endpoint resolved to the reader for an HTML listing. The generic reader's name is now `site`, the
  kind it answers to, which leaves one rule and no fallback: a *named* reader nothing answers to
  still resolves to nothing, because falling through to the kind is C4 itself. `Readers.Shipped()`
  is the one registry and every test reads it rather than a list of its own.
- **`SearchGated` describes `SearchUrl`, and nothing else.** A source whose search *is* its own
  address — 1337x carries `{query}` in the one address it has — is gated by `Gated`.
  `SearchAddressGated` says which applies. The health tool's first real run sent all four gated
  sites down plain HTTP and every one of them answered with a challenge.
- **The manifest declares `targetAbi` `10.0`, not the `10.1` the specs said.** The server's
  `PluginAbi.Current` on `dev` is `10.0` and `AbiVerificationStage` is enforced, so `10.1` is refused
  at load. `docs/01-plugin.md` and `docs/reference/README.md` are corrected. `ManifestTests` asks
  `PluginAbi.IsCompatible` rather than a literal, so this cannot drift again.
- **`DefaultTrackers` ships empty.** `docs/04-domain.md` said "a shipped list" and no document said
  which. Corrected there; the choice is the owner's, and it is under **Blocked** above.
- **Secrets never enter the settings object.** A passkey lives at `tracker:{id}:passkey` and an API
  key at `indexer:{id}:apikey` in `IPluginSecretStore`; the settings carry an announce URL with
  `{passkey}` standing where the secret goes. `docs/04-domain.md` and `docs/08-ui.md` now say so.
- **`AppContext.BaseDirectory` and the assembly's folder are the same directory under a test run**,
  so no ordinary test can tell C1's fix from C1. `AssemblyFolderTests` loads a copy of the assembly
  in its own `AssemblyLoadContext` from another folder, which is the only way they differ in
  process — and the only test a `BaseDirectory` regression fails.
- **The whole browser chain works end to end on this machine.** `CreateDesktop`, `CreateProcess` with
  `lpDesktop`, the Chrome download and the Puppeteer connection were all exercised for the first time
  taking the captures, and all four gated pages came back.
- **"Not a challenge" is not "ready".** A challenge clears by navigating, and in between the tab
  holds a document that is neither page: 1337x answered 876 bytes of stylesheet links and no body.
- **And "ready" is not `readyState === 'complete'`.** An indexer is full of third-party requests that
  never finish, so waiting for the load event times out on a page readable for forty seconds. The
  signal that works is a parsed document with a body that has children in it.
- The browser driver is **PuppeteerSharp** 25.6.0, connected to the browser this plugin started —
  never launching one, since a driver knows nothing about hidden desktops. `Puppeteer.ConnectAsync`
  with `BrowserURL = http://127.0.0.1:{port}`; `IPage.ReloadAsync` takes `ReloadOptions`, not
  `NavigationOptions`; `IBrowser.Disconnect()` is synchronous; `InstalledBrowser` is in
  `PuppeteerSharp.BrowserData`.
- `CA1416` is on and enforced: a Windows-only or Linux-only type must carry `[SupportedOSPlatform]`
  and be constructed behind an `OperatingSystem.IsWindows()`-style check, not behind an equivalent
  the analyser cannot follow. It caught two real cross-platform slips in `S2-03`.
- A test that fetches **one host twice** cannot use a fake clock at all: the second request waits on
  a gap nothing will advance. Use the real clock with a nought interval.
- The mutation harness under-reports failures whose test names carry `[Theory]` arguments. When it
  says NOTHING FAILED for a rule that plainly has a test, run that one mutation by hand before
  believing it — one such report was wrong, and the rule was covered eight times over.
- A test that waits on a `FakeTimeProvider` must **bound** the wait. A gate regression leaves the
  wait unsatisfiable, and an unbounded one hangs the suite rather than failing it.
- `PluginGrantKind.NetworkHost` is `"network.host"`; `IPluginGrants.RequestAsync(kind, scope, reason,
  ct)` takes the host as the scope.
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
- The plugin contract is packed from the **released** server, never from `dev`. `0.1.404` is the dev
  version and never moves; the released one does. Reading `dev` for it is how the contract sat at
  `0.1.404` while the server shipped `0.1.478`, and every contract added in between — the table
  action cell among them — was invisible here.
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
- **All sixteen enabled sources answered on 15 August 2026**, gated ones included, and every reader
  read what its source sent. `health/report.md` is written by `dotnet run --project tools/SourceHealth`
  and is not committed.
- A Chrome left behind by a tool run that was killed keeps the profile and port 9222, and a later
  run's browser hands off to it and exits — so "Starting the browser" appears once per gated source
  and every tab belongs to the old one. Kill strays under `_capture\browser` before believing a
  health run. A run that finishes normally leaves none.
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

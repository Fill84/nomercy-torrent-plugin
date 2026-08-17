# Where the work is

Read this first, update it last. Nothing else decides what happens next.

## Current

**Sprint 5 — BitTorrent**
**Slice `S5-08` · Encryption** — not started.

Specification: `docs/plan/SPRINTS.md`, section `S5-08`. `docs/06-torrent-client.md` § Encryption is
the spec. Its first step wants **a captured MSE exchange**, and no peer on this network has held a
conversation yet — read the note under **Decisions** before starting, and take a capture first.

## Blocked

Nothing. Two things wait on the owner without blocking anything:

- **Sprint 4's acceptance on the real library.** The chain decides end to end and is proven against
  real captured pages, but "the dashboard shows what it would take for every missing episode" needs
  0.4.0 on `beast-unit` and a stopped server. It runs with no torrent client and says so per episode,
  which is exactly what dry run shows; say when, and it can be deployed and watched.
- **Sprint 1's acceptance against the real library.** `HandCountedLibraryTests` proves the chain
  against a library counted by hand, but "the Shows page matches *the* library" needs 0.4.0 on
  `beast-unit`, and a deploy needs the server stopped. Say when, and it can be checked against the
  ~25 shows and ~42 missing episodes recorded under **Facts**.


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
- **One full-suite run during `S5-07` failed seven tests and would not reproduce.** It came straight
  after an eighteen-mutation sweep; five later runs, including the same format-build-test chain, were
  green, and no trx was kept, so there are no names to work from. It is written down rather than
  waved away: if it happens again, run with `--logger "trx"` first and the names will say whether it
  is the stale-assembly fault above or something to do with sockets after that many back-to-back runs.
- **A bitfield is high bit first**: the top bit of the first byte is piece nought, which is the
  opposite of what a bit index usually means. A client that got it backwards would ask every peer for
  what they do not have and refuse what they do.
- **`EndgamePieces` is eight and no document gives a number.** It is the point at which the last
  pieces are asked of every peer at once; the tail of a download is otherwise spent waiting on the
  slowest peer holding the last one. Written as a documented constant in the picker.
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
  test file — and the capture tool is already written for the day a peer will talk. Worth checking
  whether this machine's outbound connections to high ports are filtered: `S5-07` needs a peer that
  answers.
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
- **At most `MaxSearchAttempts` names are searched for per episode per cycle.** Twenty spellings of
  one release times seventeen indexers is a cycle that gets the plugin banned. The setting already
  means "how many times an episode is looked for", so it is that number rather than a new one.
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

# The store — design

**Decided 2026-08-08.** Subsystem B of four, after the engine (A) and before the orchestrator (C)
and the surface (D).

What the plugin remembers between runs: which episodes are missing, what it grabbed for them, what
is downloading, and what it has learned not to try again.

---

## 1. Decisions

Answered by the owner, and recorded so they are not guessed at later.

| Question | Answer |
| --- | --- |
| Which shows are watched? | **Every show in the library, automatically.** No opt-in per show |
| Where does state live? | **One file in the plugin's data folder**, written atomically. SQLite was chosen first and then withdrawn - see below |
| Upgrade an episode when something better appears? | **No.** Once an episode is in, it is done |
| When does it search? | Cron, on a library change, on a button in the UI, and around an expected air date |

### The consequence of watching everything

A library with a long-running show and years of gaps has hundreds of missing episodes on the first
run. Watching everything means all of them are wanted at once.

That is the owner's decision and it stands. What follows from it is a design constraint rather than
a second opinion: **the queue is bounded.** A fixed number of grabs may be in flight, and the rest
wait. A first run becomes a steady stream over hours instead of several hundred simultaneous
downloads competing for the same connection, the same disk, and the same swarm.

### Why not SQLite, in the end

SQLite was the owner's choice, and it was withdrawn on a fact found while building: the host does
not share `Microsoft.Data.Sqlite` with plugins. Its shared set is `NoMercy.Plugins.Abstractions`,
`NoMercy.Plugins.Mvc`, `NoMercy.Events`, `NoMercy.Design`, logging, dependency injection and
Newtonsoft - and nothing else.

Using SQLite would therefore mean shipping a second `Microsoft.Data.Sqlite` and a second native
`e_sqlite3.dll` into a process that already has the media server's. Separate load contexts make that
*probably* fine. "Probably fine" is not a thing to be on somebody's media server for, in exchange
for a query planner over a few thousand rows.

So the store is one file, held in memory and written atomically on change - the same pattern
`FileResumeStore` already runs and which is already proven. It needs no package at all, which means
it lives in `Core` rather than the shell, and `Core` keeps the zero-dependency property that lets
every test run with no host present.

The seam is still what matters: **`Core` owns `IDownloadStore`**, the orchestrator is written
against it, and the test suite is written against the interface rather than either implementation -
so the in-memory store every orchestrator test uses cannot drift from the real one.

## 2. What is stored

Four tables, and deliberately not a fifth. There is no `monitored_shows` table because nothing is
opted in: the library is the list, and the plugin's job is to notice what is missing from it.

### `wanted_episodes`

One row per episode the library knows about and does not have a file for.

| Column | Why |
| --- | --- |
| `show_id`, `season`, `episode` | Identity, and the natural key |
| `show_title`, `episode_title` | So the UI can render a queue without asking the server for every row |
| `air_date` | Drives the search-around-air-date trigger |
| `state` | `Wanted`, `Searching`, `Grabbed`, `Done`, `Unavailable` |
| `last_searched_at` | So a search cycle can skip what it just looked at |
| `search_attempts` | To back off on something nobody is seeding rather than asking forever |

Rows are refreshed from the library rather than being the truth. If a user deletes an episode, the
next refresh wants it again; if a user adds a file by hand, the next refresh stops wanting it.

### `grabs`

One row per release this plugin decided to download. The audit trail of what it did and why.

| Column | Why |
| --- | --- |
| `id`, `info_hash` | Identity. The info hash is what ties a grab to a torrent in the engine |
| `show_id`, `season`, `episode` | What it was for |
| `release_title`, `indexer`, `size_bytes` | What was chosen, for the UI and for a later complaint |
| `state` | `Grabbed`, `Downloading`, `Downloaded`, `Imported`, `Failed` |
| `grabbed_at`, `finished_at` | Duration, and for pruning old rows |
| `failure_reason` | Null unless it failed, and then it says what actually happened |

The invariant from the plugin's existing design holds here: **an incomplete handoff is never
recorded as a finished one.** `Imported` is written after the file is in the intake, never before.

### `transfers`

What the engine is doing right now, mirrored so the UI can be drawn without waking the engine and
so progress survives a restart.

| Column | Why |
| --- | --- |
| `info_hash` | Ties to the grab and to the engine's session |
| `bytes_done`, `bytes_total`, `peers` | What a progress bar needs |
| `updated_at` | To notice a transfer that has stopped reporting |

### `blacklist`

What not to try again.

| Column | Why |
| --- | --- |
| `info_hash` or `release_title` | Either identifies a bad release; some sources give no hash |
| `reason` | Failed to download, failed verification, imported as the wrong thing |
| `added_at`, `expires_at` | A release that failed once may be fine next month; a permanent blacklist rots |

## 3. What the store is not

- **Not a cache of the library.** `wanted_episodes` is derived and disposable. Losing the database
  loses history and in-flight state, not the user's media.
- **Not the engine's resume record.** That is `FileResumeStore`, beside the data, already built and
  deliberately separate: a database row and a bitfield have different durability needs.
- **Not a queue implementation.** Ordering and the in-flight limit belong to the orchestrator. The
  store answers questions; it does not decide what happens next.

## 4. Testing

Same rule as everywhere else: the interface is in `Core` and the orchestrator is tested against an
in-memory implementation, so no test needs a file.

The file implementation gets its own tests on top, covering what only something touching disk can
get wrong: state surviving a reopen, an unreadable file starting over instead of refusing to run,
concurrent writes not losing each other, and no temporary file left behind.

Every test must fail if the behaviour breaks.

## 5. Build order

| # | Slice | Proves |
| --- | --- | --- |
| 1 | `IDownloadStore` and the record types in `Core` | The orchestrator can be written against it |
| 2 | `InMemoryDownloadStore` in the test project | Every later slice is testable with no file |
| 3 | Wanted-episode refresh from a library snapshot | A deleted file is wanted again; a restored one is not |
| 4 | Grab lifecycle and its invariant | `Imported` cannot be written before the file is in intake |
| 5 | Blacklist with expiry | A failed release is skipped, and is retried once it expires |
| 6 | `FileDownloadStore` in `Core` | The same contract suite passes against a real file |
| 7 | Durability | State survives a reopen; an unreadable file starts over rather than refusing to run; concurrent writes do not lose each other |

**Status 2026-08-08: all seven built and green.** The file carries a version number so a future
change of shape has somewhere to branch on.

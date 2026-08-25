# Audit of 0.3.9

25 August 2026. A full read of the shipped source before 0.4.0, looking for work done twice, code
nothing reaches, and rules written down in more than one place.

## What was read, and how

27,241 lines across three source projects, and the 27,946 lines of tests that hold them up.

| Project | Files | Lines | Audited for |
| --- | --- | --- | --- |
| `NoMercy.Plugin.TorrentDownloader` | 47 | 10,470 | everything |
| `NoMercy.Plugin.TorrentDownloader.Core` | 59 | 7,739 | everything |
| `NoMercy.Plugin.TorrentDownloader.Bittorrent` | 42 | 9,032 | dead code only — see below |

Four passes, each mechanical so it can be run again:

1. **Declared types against references.** Every `public`/`internal` type in `src`, counted across
   `src`, `tests` and `tools`.
2. **Public methods against callers.** The same, with constructors and record members excluded.
3. **Calls per tick.** Every `await` on a repository, a port or the engine inside the cadence that
   runs every minute, counted by target.
4. **One rule, two places.** The policies this plugin turns on — what makes a show the owner's, what
   makes a file a video — traced to every expression of them.

**The BitTorrent client was read but not judged on behaviour.** It is proven and the owner has ruled
it out of scope for change. Dead code found in it is listed so nothing is lost, and nothing is
proposed for it.

## What is already right

Worth saying, because the findings below are short and could otherwise read as a poor report card.

- **No `TODO`, no `FIXME`, no commented-out code, no `NotImplementedException`** anywhere in `src`.
  The definition of done in `CLAUDE.md` is met.
- **283 declared types, 12 of which are named only in their own file** — and all twelve are
  legitimate: request records bound by MVC, one exception type, and private helpers.
- **Every view builds through `Ui`.** There is not one `new PluginComponent` outside that file, so
  the design system is applied in one place rather than page by page.
- **The live push is debounced.** `LiveSnapshot.Changed` coalesces a burst of journal events into
  one push, so a busy cycle does not turn into a message per event.
- **Ports where it matters.** `ITorrentEngine`, `ILibrary`, `INamePool`, `ISourceLedger` and
  `ICycleJournal` keep Core free of the host. One exception, and it is finding F1.

## Findings

Severity is what it costs if left, not how hard it is to fix.

### A · One rule, two places

**A1 — The rule that decides which shows are the owner's is written twice.** *High.*

```
src/…Core/Pipeline/MissingRefresh.cs:65   if (!episodes.Any(episode => episode.HasFile))
src/…/Hosting/Transfers.cs:361            has = (await library.GetEpisodesAsync(show, ct)).Any(one => one.HasFile);
```

One decides which shows are tracked and searched for; the other decides which grabs are cancelled
for belonging to a show the owner does not have. They are the same policy and they must agree — if
they ever disagree the plugin grabs a show and then cancels it, or keeps one it should not.

This is the rule that, when it was changed in one place on 24 August 2026, put the plugin on 479
grabs in an afternoon with 456 of them Family Guy. It is also the rule that **changes** when
media-server #36 lands and library membership becomes the discriminator. Two places to change is one
too many.

### B · The same work, more than once a tick

The transfers cadence runs **every minute**. Everything here is per-tick cost with no behaviour
attached.

**B1 — The library's shows are fetched once per item instead of once per tick.** *Medium.*

`Transfers.StageAsync` and `Transfers.DispatchAsync` each open with

```csharp
Show? show = (await library.GetShowsAsync(ct)).FirstOrDefault(candidate => candidate.Id == …);
```

so a tick staging four episodes and dispatching four makes eight host round-trips for a list that
cannot change inside one tick.

**B2 — Two separate caches of the same episode lookup.** *Low.*

`NotOursAsync` builds its own `Dictionary<int, bool>` of "does this show have a file", and
`FinishAsync` asks `library.GetEpisodesAsync` again for its own purposes. One tick, one question,
two answers fetched.

**B3 — The open grabs are read twice a tick.** *Low.*

Once at the top of `TickAsync`, once inside `LeftBehindAsync`. The second read is deliberate and
commented — staging has happened since, and a file staged a moment ago would otherwise read as one
nothing is waiting on. It is correct. It is also avoidable if staging says what it wrote.

**B4 — Every database call makes a directory and sets a file-level pragma.** *Medium.*

```csharp
// Store.OpenAsync — runs on every single call
Directory.CreateDirectory(_dataFolderPath);
…
await Execute(connection, "PRAGMA journal_mode=WAL;", ct);
await Execute(connection, "PRAGMA foreign_keys=ON;", ct);
```

`journal_mode` is a property of the database *file*: once set, it stays set. The data folder exists
after the first call. With seventeen store methods and roughly fifteen calls in a transfers tick,
that is about 21,600 directory creations and 21,600 unnecessary round trips a day.

`foreign_keys` is genuinely per-connection and must stay.

**B5 — The settings are re-read from the host on every tick and every page.** *Low.*

`SettingsStore.LoadAsync` calls `GetConfigurationAsync<Settings>` with no cache. It is a small read
of data that changes when an owner presses save, asked for at least once a minute. Worth caching,
but only with invalidation on save — a stale settings cache is worse than the round trip.

### C · Cadences that do not carry their name

**C1 — Maintenance does nothing that Search does not already do, and the real maintenance work lives
elsewhere.** *Medium.*

```csharp
case JobNames.Maintenance:  await RefreshAsync(work.Token);  break;   // "0 4 * * *"
```

That refresh already happens before every search cycle — four times a day on the default
`0 */6 * * *`. Meanwhile:

- `PruneHistoryAsync` rides inside `RefreshAsync`, so history is pruned as a side effect of a
  refresh rather than as maintenance;
- `DeduplicateAsync` runs on the first transfers tick after a start, guarded by a `_refreshed` flag.

Three pieces of periodic housekeeping, none of them in the cadence named for it, and one cadence
whose whole body is a duplicate.

### D · Code nothing reaches

**D1 — Three unused view helpers.** *Low.*

`Ui.List`, `Ui.Container` and `Ui.EmptyState` have no caller.

**Corrected on 25 August 2026 while closing this in S10-05.** This finding also said "pages render
their empty states by hand while a helper for it sits unused", and that is wrong. Not one page draws
an empty state by hand: every "nothing here" in the plugin is a table's own empty message, passed
through the single `Ui.Table` helper, and the two places that could have used an `EmptyState` carry
a comment saying why they must not — `EmptyState` is for a plugin with nothing configured, and an
idle plugin with nothing in flight is working correctly. `docs/08-ui.md` has said so all along.

So there was no drift to stop, and `EmptyState` is unused for the same reason as the other two: this
plugin has no page state that wants one. All three went.

**D2 — Three unused members in the BitTorrent client.** *Report only.*

`Dht.BootstrapAsync`, `RequestLedger.Cancelled`, `TorrentRun.ResumePoint`. Listed so they are not
lost. Nothing is proposed: the client is proven and out of scope.

**D3 — No other dead code.** Twelve types are named only in their own file; each was checked and
each is legitimate.

### E · Documents that no longer describe the code

**E1 — `SPRINTS.md` S9-03 "Every show in a library is in scope" is marked done and was reverted the
same day.** *Medium.* A reader following the plan would reintroduce the 479-grab fault.

**E2 — Two slices are called "Release 0.4.0"** (S8-05 and S9-06). *Low.*

### F · Making the coming contract a drop-in

**F1 — The encode path is the only host dependency without a port.** *Medium.*

```csharp
public sealed class Transfers(
    ITorrentEngine engine,       // port
    GrabRepository grabs,
    ILibrary library,            // port
    Stager stager,
    EncodeDispatch dispatch,     // ← the concrete class
    …
```

Every other thing this plugin asks of the server sits behind an interface in `Core/Ports`. The
encode does not, and it is precisely the part that changes when media-server #30 gives plugins
`IPluginEncoder` and #35 gives them the episode's id.

With a port, that day is a new class beside the old one and one line of composition. Without it, it
is surgery on `Transfers`. This is not a seam invented for testing — it is the pattern this codebase
already uses everywhere else, missing in the one place a change is already scheduled.

## Summary

| | Finding | Severity |
| --- | --- | --- |
| A1 | The owner's-show rule is written twice | High |
| B1 | Library shows fetched per item, not per tick | Medium |
| B4 | mkdir and a file-level pragma on every database call | Medium |
| C1 | Maintenance duplicates Search; real housekeeping is scattered | Medium |
| E1 | A reverted slice is still marked done | Medium |
| F1 | The encode path has no port, and is the next thing to change | Medium |
| B2 | Two caches of one episode lookup | Low |
| B3 | Open grabs read twice a tick | Low |
| B5 | Settings re-read every tick | Low |
| D1 | Three unused view helpers | Low |
| D2 | Three unused BitTorrent members | Report only |

Nothing here changes what the plugin does. Every item is either work removed, a rule moved to one
place, or a seam put where the next change lands.

# One cycle, driven by events

**Decided with the owner on 13 September 2026.** This replaces the four cadences, the one-minute
transfers job and every internal poll that stood in for something able to say so itself.

## What the owner asked for

> Er mag van alles gevraagd worden — maar niet *continu*. Dat is resourceverspilling, terwijl het
> gewoon met async of events kan.

and, when asked where the schedule should live, the shape of the whole thing:

> feed → feed klaar → search → search doet wat die moet doen en downloads direct starten → download
> klaar → download verplaatsen → verplaatsen klaar → encode job dispatchen → volledige run klaar
> alles gedownload en geen encodes meer → maintenance.

That is not four schedules. It is **one cycle**, and each step is started by the previous one
finishing.

## What is actually there today, measured

This is what the plugin does on the owner's server right now. It is written down because most of it
is not visible from any one file.

**`BittorrentEngine.StatusAsync` is not a read.** It is the client's entire housekeeping, hidden
inside the method pages call to draw a table:

```csharp
foreach (Held held in _torrents.Values)
{
    Expire(held, now);   // give up on a magnet no peer will serve
    Stalled(held);       // notice a download that has stopped
    Seeded(held, now);   // notice completion, and stop seeding when the policy is met
}

Queue();                 // start whatever the concurrency limit allows
resume?.Tick(...);       // write the resume files
```

Nothing else calls those five. So the *real* reason `JobNames.TransfersCron` was `* * * * *` is not
transfers at all — it is that without a tick every minute the client stops expiring, stops noticing
stalls, stops noticing completions, stops obeying its own concurrency limit and stops writing resume
files. **Removing the cron without moving those five would break the client.** That is the single
most important finding of this audit.

**Completion is noticed, never announced.** `Seeded` is reached only from `StatusAsync`, and
`TorrentRun.Progress().Complete` is derived on demand. The moment a download actually finishes —
`TorrentSession` line 628, `verified.Set(piece)` — nothing is told.

**The encoder is asked, not heard.** `Transfers.StandingAsync` calls `IEncodeJobs.StatusAsync` once
per job per tick. The media server has published `EncodingCompletedEvent` and `EncodingFailedEvent`
all along.

**The page heartbeat runs for the life of the server.** `Heartbeat` sampled `BittorrentEngine.Drawn`
once a second from the moment the client started, on every server, downloading or not, page open or
not. Each reading takes the client's lock and builds a string over every torrent. It is gated on
`Watching` now — a client holding nothing is asked nothing — but it still runs for a torrent nobody
is looking at, and every reading that differs is a push, and every push makes the web app re-fetch
the entire view over HTTP. A running download therefore costs one full page load a second, whether
anyone has the page open or not. That is the flood the owner saw.

**`BittorrentEngine.Moving` is dead.** Its own remark says "read once a second by the page's
heartbeat"; the heartbeat reads `Drawn`. Nothing in `src/` refers to it. Only tests do.

## The two facts that make the encoder half possible

Both were doubted, and both were checked in the media server rather than assumed.

**`EncodingCompletedEvent.JobId` is not a job id.** It is `fileMetadata.Id`, which
`VideoEncodeJob.GetFileMetaData` sets to `movie?.Id ?? episode!.Id` — the media row id. That is the
same `mediaId` this plugin hands to `IPluginEncoder.EncodeAsync`, which `PluginEncoder` puts on
`VideoEncodeJob.Id`. So an event can be matched to the grab that asked for it. Every
`EncodingFailedEvent` raised by `VideoEncodeJob` carries the same value, via
`EncoderCardTerminator.PublishFailedAsync(fileMetadata.Id, …)` or `Id.ToInt()`.

**`PluginEncodeResult.JobId` is something else entirely** — `QueuePayloadHash.For(payload)`, a hash
of the job's payload, chosen deliberately because a queue row id is not stable: a finished job is
deleted and a failed one is rewritten under a new identity. It can never be matched against the
event. The match is on the media id, and nothing else.

## The media server has no periodic library scan

Checked, because the owner believed otherwise and the whole trigger design rested on it.
`ServiceConfiguration.Cron.cs` registers nine cron jobs — certificate renewal, activity-log
retention, TMDB changes, device drop rules, user sync, database backup, IP-ban expiry, audio
analysis, derived-audio eviction — and none of them scans a library. `LibraryScanJob` and
`LibraryRescanJob` are dispatched only from `LibrariesController`, which is the dashboard, and after
an import.

`LibraryFileWatcher` does watch library folders live and raises `FileCreatedEvent`. It must **not**
be the trigger: an encode landing an episode in the library raises it, so the plugin would trigger
itself for ever.

## The model

**One cycle, open until the work is done.**

| Starts a cycle | |
| --- | --- |
| The **Run now** button | the owner asked for it |
| `LibraryScanCompletedEvent` | the owner finished a scan on the dashboard |
| The owner's own cadence | one setting, default hourly |

A trigger arriving while a cycle is open is **not** a second cycle and is **not** dropped. It is
added to the open one, carrying the exclusions the open cycle has already made, so nothing is
grabbed twice. Maintenance is postponed until there is nothing left to do.

**Done means:** nothing downloading, nothing waiting to be staged, nothing staged waiting for an
encode, no encode outstanding — and no private torrent still seeding. A public torrent never uploads
from this client, so it is finished the moment it is complete and does not hold the cycle open. A
private one is seeding a debt to a tracker and counts as work in hand.

*Consequence, stated once:* a private torrent left to reach a ratio can hold a cycle open for days,
and maintenance — which prunes the history and sweeps orphaned download folders — waits that long.
That is the owner's choice, made knowingly on 13 September 2026.

## The chain, end to end

Every arrow is an event or a completed `Task`. Nothing on this line is a timer.

```
trigger ─▶ feed ─▶ search ─▶ grab ─▶ engine.AddAsync
                                        │
                          last piece verified (TorrentSession)
                                        │
                              TorrentRun.Finished
                                        │
                            BittorrentEngine.Completed(infoHash)
                                        │
                                     stage ─▶ dispatch encode
                                        │
                       EncodingCompletedEvent / EncodingFailedEvent
                                        │
                                  episode in library
                                        │
                             nothing left in hand ─▶ maintenance
```

## Moving the five out of `StatusAsync`

`StatusAsync` becomes a pure read: it draws what is, and changes nothing. Each of the five gets the
driver it should always have had.

| Was, per tick | Becomes |
| --- | --- |
| `Refuse`, `Cramped` | `TorrentRun.Opened`. **Not the metadata arriving**, which is sooner and is not enough: whether a torrent holds a video file at all is decided while its session is opened, and a fake release has perfectly good metadata. |
| `Seeded` | The `Finished` event. The seed-hours half is the torrent's one deadline, set at completion; the ratio half is `TorrentSession.TellMeWhenGivenBack` — once a download has stopped moving a ratio is a fixed count of bytes, so the session says when they have gone out instead of being asked what the ratio is. |
| `Queue` | Add, settle, finish, pause, resume, remove. |
| `resume.Tick` | A verified piece, a run settling what it holds, a torrent finishing, the owner pausing one. `ResumeKeeper` keeps the owner's interval, so the ones that are too soon cost a comparison. **A verified piece alone is not enough:** a torrent already whole on disk never verifies another, and those are the torrents whose resume file matters most. |
| `Expire`, `Stalled` | The torrent's one deadline. |

### The one deadline, and why it is not a poll

The owner asked for no timers at all. Three things here cannot be told by an event, because they are
about something *not* happening: nobody sends a message saying they will not serve a magnet's
metadata, nothing announces that no byte has arrived for twenty minutes, and the passing of two hours
is itself the seeding condition. `MetadataTimeoutMinutes`, `StallMinutes` and `SeedHours` are the
owner's own settings and all three are durations. A duration is measured by asking over and over —
the poll this work exists to remove — or by waking once at the end of it.

So each torrent holds **one** timer, set to the earliest of what that torrent owes, and `Moved`
pushes it back every time a piece really verifies. On a download that is running it never goes off.
A client holding nothing has no timer at all. Shown this on 13 September 2026, the owner kept all
three: without them a release that never arrives is never given up on, and the episode it was for is
never searched for again.

**No ordinary client does this, and the reason is instructive.** qBittorrent leaves a magnet on
"fetching metadata" for ever and shows a dead torrent as "stalled" in a list, because a person
decides what to do about it; libtorrent, its engine, runs `session_impl::on_tick()` every second for
everything. Here there is nobody watching.

## What the web app is told

The owner's rule: **only while a page is open, and only when something a page draws has changed.**

A push is only ever a signal — `PluginScreen.vue` answers any message by re-fetching the whole view
over HTTP — so a push nobody needs is a full page load nobody needs.

- The plugin cannot ask the hub who is watching. `PluginHub.Subscribe` adds the connection to
  `plugin:{ulid}` and tells the plugin nothing.
- It does not need to. **Rendering the view is the proof.** A page that is open re-fetches on every
  push, so the watermark renews itself for as long as anyone is looking, and goes stale on its own
  when the last page closes.
- The byte heartbeat runs only while a torrent is held **and** a view was rendered recently.
- State changes — finished, failed, added, paused — push regardless. They are rare, and they are the
  ones worth arriving.

`Drawn` already covers rates, peers, seeds, chokes and the error, so a stalled torrent still moves it
and still counts as news: `S11-29`, and it survives this.

## What is deleted

Dead code and red herrings, all of it named so none is missed:

- `BittorrentEngine.Moving` — referred to by no shipped code.
- `IEncodeJobs`, `HostEncodeJobs`, `EncodeGateway.JobsOf`, both `Transfers.StandingAsync` overloads
  and `Transfers.Named` — the plugin is told now, so it never asks.
- `JobNames.TransfersCron`, `JobNames.FeedCron`, `JobNames.SearchCron`, `JobNames.MaintenanceCron`
  and the three-way `Clock.DueAsync` over them — there is one cadence now.
- `TorrentDownloaderPlugin.StartDueCadences`, `TickDueCadencesAsync`,
  `TickDueCadencesGuardedAsync`, `RunFeedAsync`, `RunSearchAsync`, `RunMaintenanceAsync` and
  `RunCadenceAsync` — the cycle is one chain, not three guarded passes.
- The feed, search and maintenance cadence fields on the Settings page, and their halves of
  `SettingsEdit` and the `cadences` table. One field replaces them.

## The slices

Each is red-first, and each leaves the suite green.

1. **`S12-11` The session says it is finished.** `TorrentSession.Finished`, raised once, outside the
   lock, where the last wanted piece verifies — and for a torrent that is already complete when it
   is opened, which is what a restart finds. **The S11-37 deadlock lived in exactly this code**: a
   wait taken inside the run's lock that only something holding the same lock could satisfy. The
   raise is outside every lock and the test proves it.
2. **`S12-12` The engine says which torrent finished.** `TorrentRun.Finished`,
   `BittorrentEngine.Completed(infoHash)`, subscribed per run as it is taken on.
3. **`S12-13` `StatusAsync` becomes a read.** The five move to their own drivers, per the table
   above. Nothing about what the pages draw changes.
4. **`S12-14` The encoder is heard, not asked.** *Done.* `EncoderSays` subscribes to
   `EncodingStartedEvent`, `EncodingCompletedEvent` and `EncodingFailedEvent`, matched on the media
   id, listening from the moment the plugin is loaded — a listener is only worth anything for having
   been listening, and a restart part way through an encode is when that matters. The asking path is
   gone entire, the stored job id with it, and migration 011 takes its column: kept, it would be a
   field nobody reads that looks exactly like the one to match an event on.
5. **`S12-15` One cycle.** *Done.* Three triggers — Run, `LibraryScanCompletedEvent`, the owner's
   cadence — all through one door; a trigger during an open cycle is added to it; maintenance when
   nothing is left in hand. The four cadences are one setting, and the plugin's own clock is set to
   the moment the next cycle is due rather than woken to ask whether one is. The host's tick starts
   nothing: it only makes sure that clock is wound, because the contract has no way to decline a
   schedule. The four retired job names are still answered to and start nothing, so an upgrade that
   leaves them registered is not four stack traces an hour.
6. **`S12-16` The pages are told only when someone is looking.** *Done.* Not a watermark on a
   clock but `Onlookers`: a fetched page says somebody is looking, a push unanswered for fifteen
   seconds says nobody is, and ten minutes with nothing to push rests the watch until the client
   stirs. The heartbeat's timer exists only while somebody is looking, and its first reading after a
   page opens is a baseline that pushes nothing.
7. **`S12-17` One cadence field.** The Settings page, `SettingsEdit`, the migration.

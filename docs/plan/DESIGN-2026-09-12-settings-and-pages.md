# The settings and the pages, rebuilt — 12 September 2026

The owner's decisions of 12 September 2026, taken after the live run of the night before. This
document is the design; `docs/04-domain.md` § Settings and `docs/08-ui.md` are corrected to match it
when the work lands, and it is those two that stay the specification afterwards.

## The one sentence

Every setting on the page is one the owner would really change, it does what it says, and what is
expert sits behind **Show advanced**.

## What leaves, and what happens instead

| Leaves | What happens instead |
| --- | --- |
| `MaxSearchAttempts` | Nothing. Every gap is searched on every run, for ever. |
| `MinimumSeeders` | No seeder gate at all. A copy is taken on its name; the count still ranks copies. |
| `SeasonPackThreshold` | No threshold. A pack is taken when it is the best copy of the gap being looked at. |
| `AllowSeasonPacks` | Fixed on. A pack is an ordinary copy. |
| `DryRun` | Gone from the page and the stored settings. |
| `PortMapping` | Gone as a switch. The router is always asked for the configured port. |

**Why the attempt limit goes, and what it costs.** It never worked: an episode was marked
`Unavailable` after its search and the refresh at the start of the next run derived it as `Missing`
again, keeping the attempt count. The owner's Freak Brothers episodes stood at 67–69 attempts on
11 September 2026. Asked whether it should hold for a time, the owner chose to drop it outright.
The cost is real and accepted: six episodes nobody serves cost about two minutes each, every run.

**Why the seeder gate goes.** The owner's words on 12 September 2026: there is no threshold,
download what you find; an episode taken when it airs has seeds. A count is still read, still shown
and still decides which of two copies wins — it just never refuses one. A copy nobody is serving
starts and the stall rule (no progress **and** no peers for `StallMinutes`) ends it, which is the
rule that already existed for it.

**Why dry run goes.** It is a testing switch on an owner's page. `CycleOptions.DryRun` stays in the
pipeline because the tests decide a whole cycle with no client behind it; nothing writes it from
settings and no page offers it.

## What stays, and where

**Settings page, in sections, each with its own Save** — a bad value in one section never blocks
another, which is how the forms already work.

| Section | Holds |
| --- | --- |
| Folders | incomplete, intake. A folder that cannot be written says so; different volumes warn, because every completion then pays a full-file copy. |
| Quality | resolution, codec, "refuse a release that does not say which codec it is", English only, include specials, forbidden terms. |
| Torrent client | downloads at once, download limit, upload limit, listen port with its state. |
| Private trackers | the owner's own: host, announce URL carrying `{passkey}`, the passkey write-only. Seeding appears here, and only here. |
| Advanced | stall minutes, metadata timeout, encryption, resume interval, the four cron expressions. |

**Codec keeps both controls.** The owner was asked whether to merge them and said no. The second
one is still only consulted when a codec is named, which is what `Profile.CodecTagRequired` already
says.

**Speed limits are presets with a box.** Unlimited, 1, 5, 10, 25 MB/s, and a box for anything else.
Stored in bytes per second exactly as now, so nothing downstream changes; the page stops asking the
owner to type 10485760.

**Seeding appears only with a private tracker.** Seed ratio, seed hours and maximum upload apply to
private torrents alone — nothing public is ever uploaded, `docs/06-torrent-client.md` § Uploading —
so with no private tracker configured the three controls are not drawn at all, and a line says why.

## Advanced

One **Show advanced** switch at the top of a page, remembered per page. Every advanced block on that
page appears with it. It is a display state, not a setting: it is not written to `config.json` and
it changes nothing about what the plugin does.

## The listen port

**Default 6881 on a fresh install**, which is the BitTorrent default (6881–6889); 51413 was
Transmission's and is what this plugin shipped with. An existing install keeps whatever it has.

The router is asked for the configured port over UPnP, then NAT-PMP, exactly as now. The page then
says one of three things, and only one of them is a warning:

| State | Shown | When |
| --- | --- | --- |
| Open | the port, plainly | a peer has dialled in from outside, or a live check says it answers |
| Not known yet | the port, plainly | nothing has proved it either way |
| Shut | the port, with a warning icon | a live check says it does not answer |

**A failed UPnP or NAT-PMP attempt is not a warning.** On the owner's network neither protocol ever
answers and the port is forwarded by hand, so mapping failure says nothing about reachability.
That is why it no longer produces a line on the page.

**Until the server can check an arbitrary port, "open" has one proof: a peer dialling in.** An idle
server therefore reads *not known yet*. `INetworkDiscovery.IsPortOpenAsync()` exists but is wired to
the server's own external web port; media-server issue #52 asks for overloads taking a port, and
issue #53 covers the capability seam a plugin would reach them through. When #52 lands the plugin
resolves `INetworkDiscovery` through `IPluginContext.Services` — the path `Hosting/ShowImport.cs`
already uses — checks the real port, and the warning starts clearing by itself.

## Cadences, and the plugin's own clock

The four cron expressions become plain intervals: *every minute*, *every 5 / 15 / 30 minutes*,
*hourly*, *every 6 hours*, *daily at 04:00*. The cron box stays under Advanced for anything the list
cannot say, and an invalid expression is refused with its reason.

**A saved cadence takes effect at once, and that requires the plugin to keep its own clock.** The
host reads `IScheduledTaskPlugin.Jobs` when a plugin is installed, hot-swapped or enabled and at no
other time: `PluginCronRegistrar.RegisterPlugin` re-reads them, but only those three call it, and
`IPluginSystem` — where a `tasks` command would live — has no implementation in the server at all.
So the plugin declares **one** job, ticking every minute, and decides for itself which work is due:

- transfers on every tick, which is what its cadence already is;
- search, feed and maintenance when the owner's interval says they are due, measured from the last
  time each finished. That time is kept in a `cadences` table — one row per cadence name, holding
  when it last finished — because a restart must not make every cadence due at once, and the four
  are not all runs: `runs` is the search's own history and says nothing about feed or maintenance;
- the dashboard's *next run* is read from that same schedule, so it stops being a guess.

`Jobs` is currently cached in a field and read once; that cache goes, because a changed cadence has
to be visible to the host on the next registration too.

When issue #53 gives a plugin a way to ask for its own re-registration, the timing can be handed
back to the host and this clock deleted. The design keeps that door open by leaving the cron
expressions the stored form of a cadence.

## The other pages

| Page | Change |
| --- | --- |
| Queue | The third list, *given up for now*, goes with the state behind it. Rows keep attempts and last tried, so a hopeless episode is still visible as one. |
| Sources | Gains the switches: every shipped source and every own indexer, on or off, with priority — under **Show advanced**, all on by default. A source or indexer in use is not editable and its address is not shown. |
| Dashboard | Keeps Run now and Stop; they leave Settings. Unchanged otherwise. |
| Shows, Downloads, History, Skipped | Not in this design. They are rebuilt after Settings and Sources, each with its own questions first. |

## Stored settings, before and after

Removed from `config.json`: `Profile.MaxSearchAttempts`, `Profile.MinimumSeeders`,
`Profile.SeasonPackThreshold`, `Profile.AllowSeasonPacks`, `Client.PortMapping`, `DryRun`.

Unchanged in shape: folders, `Cadences`, the rest of `Profile`, the rest of `Client`, `Indexers`,
`PrivateTrackers`, `DisabledDefaultSources`.

**A settings file written by an older version still loads.** The file is read into `Settings` and a
key that is no longer on it is ignored rather than refused, so the owner's existing file needs no
migration. It is worth saying plainly what follows: the settings are re-serialised from that object
when anything is saved, so the removed keys leave the file on the next save rather than lingering.
A downgrade after that reads its own defaults for them.

`Client.ListenPort` keeps its stored value; 6881 is the default for a file that does not name one.

## What the database has to change

`EpisodeState.Unavailable` and `EpisodeStates.Unavailable` are removed from the domain, from
`EpisodeRepository`, from `QueueView` and from `docs/04-domain.md`'s state table. A migration
(`009`) sets every row holding `'unavailable'` back to `'missing'`; without it those rows would read
as a state nothing can parse, and `EpisodeStates.FromStored` throws on an unknown one.

`MarkUnavailableAsync` and the `maxAttempts` argument of `CycleRecord.WriteAsync` go with it.
`RecordSearchAsync` stays: attempts and last-searched are still counted and still drawn.

The same migration adds `cadences (name TEXT PRIMARY KEY, last_finished_at TEXT NOT NULL)`, which is
what the plugin's own clock reads to decide what is due. A cadence with no row has never run and is
due at once, which is the right answer on a fresh install.

## How each rule is proved

Tests first, each seen to fail before it passes, and each failing again if its rule is deleted.

| Rule | Test |
| --- | --- |
| An episode is searched however many attempts it has | a cycle over an episode with 70 attempts still asks an indexer |
| No copy is refused for its seeder count | a real captured listing whose only copy has none is taken |
| Seeders still rank | of two copies of one release, the better-seeded is chosen |
| A saved cadence applies without a restart | the clock is asked what is due after a save, with a fake time provider |
| Transfers still tick every minute | the one declared job ticks and the transfers pass runs |
| Seeding controls are drawn only with a private tracker | the settings view, rendered with and without one |
| The port warning appears only when the port is known shut | the three states, rendered from each |
| A failed mapping is not a warning | a mapping refusal renders *not known yet* |
| Advanced hides nothing that changes behaviour | every advanced field round-trips through save |
| An old settings file loads | the owner's own file, with the removed keys still in it |
| Unavailable rows come back | migration 009 over a seeded store |

## Deferred, deliberately

**Private trackers.** The owner's rule of 12 September 2026 is that adding one turns every default
source and indexer off and makes that tracker the single source of truth, and that defaults in use
are neither editable nor viewable. Their own example is an RSS feed whose address carries a secret
key as a query parameter — the key itself is a secret and appears in no document, page, log or
journal. That behaviour needs its own questions before it is designed, and it is not in this
document. Until it is, private trackers keep the shape they have.

**Shows, Downloads, History and Skipped.** Named above, designed later.

## Depends on, and what happens without it

- **media-server #52** — a port check taking a port. Without it the page says *not known yet* on an
  idle server instead of *open*. Nothing else is blocked.
- **media-server #53** — the capability seam. Without it the plugin keeps its own clock. Nothing
  else is blocked.

Neither is a reason to wait: both paths are the same page and the same settings, and the better
answer replaces the weaker one when it exists.

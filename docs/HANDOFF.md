# Handoff

Rewritten 11 August 2026. Everything below is committed locally; nothing is pushed.

## Which shows the plugin holds, and why the server has to be rebuilt

Two rules decide, and everything else follows from them:

1. **At least one episode of the show is on the server.** Measured from the episodes, not from
   the host's `HaveEpisodeCount`, which reports zero for shows with hundreds of episodes.
2. **The show is still going out** - not ended, not cancelled.

A show that fails either is recorded nowhere: no page lists it, no count mentions it, nothing is
queued for it. The one way past both is naming it in **Follow another show** on the Shows page,
which is also the only way to start a show the server holds nothing of.

Measured on the owner's server, 11 August: 67 shows in the library, **12 of them with no episode at
all** - rows from an *add content* nobody followed through on, or folders since deleted, including
The Simpsons and Family Guy. Of the 55 that remain, 35 have ended or been cancelled. So the Shows
page goes from 67 entries to 20, and only those 20 cost a search.

**Rule 2 needed a server change.** `Tvs.Status` was in the database all along; the plugin contract
did not carry it. `PluginLibraryShow.Status` and the `PluginShowStatus` enum now do, mapped in
`PluginLibraryQuery` from the provider's wording, and `PluginAbi.Current` went to **10.1** so an
older server refuses this plugin by name instead of failing on a member it does not have. The
plugin's `targetAbi` is 10.1 to match.

That change is on media-server branch `feat/plugin-show-status`, based on **`dev`**, and it carries
the `/libraries` prefix fix too. **The server must be rebuilt from that branch before this plugin
will load** - until it is, the old server refuses the plugin outright.

### `dev` is the line, not `master`

`master` looks like the release line and is not: it holds `release: v0.1.xxx` commits and nothing
else, while every actual change is on `dev`. The server running on beast-unit reports
`0.1.472+2c800c86` - and `2c800c8` is `dev`'s head, not master's v0.1.472 tag. The two branches have
diverged; CI stamps the version at release time, which is why `dev`'s own
`Directory.Build.props` still says 0.1.404 while the build made from it calls itself 0.1.472.

`scripts/fetch-abstractions.*` default to `master` and say in a comment that this is deliberate
("the contract it compiles against should be the one those servers actually ship"). On the evidence
above that is backwards. The scripts were left alone - one server's version string is not enough to
redesign somebody's release process on - but a session that packs from the default and finds a
contract missing things `dev` has should read this paragraph before going looking.

### Two ways to pack the wrong contract

Both cost this session an hour, and neither looks like a packaging problem from the compiler error:

- **`fetch-abstractions` runs `git reset --hard FETCH_HEAD`.** Any local branch work in the
  media-server checkout is gone. Pack by hand (`dotnet pack src/NoMercy.Plugins.Abstractions/…`)
  while a change is in flight.
- **NuGet's global cache is keyed on version, and `dev` has said 0.1.404 for months.** Repacking
  0.1.404 with a changed contract into `_nupkgs/` changes nothing: the extracted copy under
  `~/.nuget/packages/nomercy.plugins.abstractions/0.1.404/` is reused, and the build fails on a type
  that is demonstrably in the nupkg. Delete the four `~/.nuget/packages/nomercy.*` folders after
  repacking, then restore.

An earlier version of rule 2 derived "still going out" from air dates. It is gone. On this library
it read a series cancelled last month as current and put a show on a nine-month hiatus in the past.

## An announced season is not a wanted season

A season the metadata provider lists with no air date on any episode is skipped. This is what
"Dune: Prophecy S02E01..E08" was - eight rows called "Episode 1" to "Episode 8", none of them made
yet, each burning twelve rate-limited searches before being parked as unavailable shortly before the
season was due to arrive.

The rule is off entirely for a library that dates nothing, because undated only means "unscheduled"
where a date is the norm. One dated episode anywhere in the show turns it on.

## Where the plugin is

The loop works end to end in code and is tested: the library decides what is missing, a
feed announces what exists, a site is searched for that exact name, the engine downloads
it, the finished folder receives it, and a `VideoEncodeJob` is dispatched the way the
dashboard's own Add content dispatches one.

**Never verified on the server:** a completed download, the move, and the encode dispatch.
Nothing has been downloaded yet, and see the next section for why nothing can be.

Test count at handoff: 963 green. Working tree clean.

## The blocker: cadences are never registered

Not a plugin bug, and **a restart does not fix it** - that was the previous handoff's
conclusion and it is wrong.

`PluginLoader.LoadPlugins` logs "Loading plugins...", awaits `LoadAllAsync`, logs one
`Plugin loaded: <name>` per result, and only then calls `IPluginCronRegistrar.RegisterAll()`.
On beast-unit that pass returns nothing: the 03:01 run of 11 August has **zero**
`Plugin loaded:` lines. Both plugins surface about two minutes later as a `PluginLoadedEvent`
from `PluginManager`, and the only subscriber is `PluginRouteSubscriber`, which attaches
controllers - hence "Plugin Torrent Downloader is now serving its own endpoints". Nothing on
that path registers cron executors.

So the pages render, the endpoints answer, and no refresh, feed, search or transfers tick
will ever fire. The fix belongs in media-server: cron registration has to react to
`PluginLoadedEvent` the way route attachment already does. It could not be written here -
the media-server checkout in this workspace is a **sparse** one holding only the four
contract projects.

## The URL

The plugin mounts as `library`, not `video`, because the section is the path: the host
builds a page's address as `{prefix-for-the-section}/plugins/{id}{route}`.

The plural is a server-side change. `PluginRoutes.PrefixFor` builds the prefix from the
kind's own word, so `library` gives `/library/plugins/…` - one character from the
`/libraries` where the app's own library pages live. The fix is a private `SegmentFor` that
maps the one exception, and it now rides on `feat/plugin-show-status` beside the contract
change, so one rebuild delivers both.

## The eight pages

All built. `Pages.cs` holds the route table, the tab bar and the page frame; every view goes
through `Pages.Page` so no page can be missing its bar.

| route | page |
| --- | --- |
| `/` | Overview - is it working, what needs me |
| `/shows`, `/shows/:showId` | Per show: missing, running, arrived. Follow one by name, unfollow one |
| `/downloads` | Only what is active, and a magnet by hand |
| `/queue` | Every wanted episode, click a row to search it now |
| `/history` | What became of each release |
| `/sources`, `/sources/:index` | Feeds and sites: what each yielded; the form is one click in |
| `/skipped` | What is passed over and why |
| `/settings` | Folders, schedules, quality, private trackers |

Four things learned about the client that shaped all of it, so they are not rediscovered:

- **A button loose in a column is stretched to the page's width.** `PluginContainer` is
  `flex-col`, which stretches its children. Every button belongs in a `Ui.Row`.
- **A note in `caption` is `text-xs` at the faintest colour there is**, running the full page
  width. Section notes are body text.
- **`PluginButton` draws every variant but `danger` identically**, so a variant cannot mark
  the current tab. The current tab is a badge.
- **A card is ten rem wide and truncates.** Useless for show and release names, and there are
  no posters to justify one. Tables where you scan, blocks where you act.

## The client throws away what the plugin says

An action's response body is discarded whatever it says, and the view is re-fetched. So
every refusal this plugin writes went nowhere and a rejected form looked exactly like a
saved one - which is how "a site's search address needs {query}" was invisible while an
address was refused for exactly that.

The plugin now keeps what the last press did and puts it on the next page it builds, once.
Anything added later that reports an outcome gets this for free; nothing should be written
that depends on a response body being read.

## Sources take the marker however it is written

`<replace>`, `%s`, `{search}`, `<query>`, `{0}` and the rest are rewritten to `{query}` on
the way in, so what is stored is what is used. An address ending on an empty query value
(`…/browse/?q=`) is filled in, because that is what pasting from the address bar leaves. A
path with no query string is left alone - a wrong guess there searches the same page forever
without ever looking broken.

## Also still open

- The resolver is built and tested but has never run against a real site.
- `BrowserIdentitySolver` passes gates that check who is asking. It does not run JavaScript,
  so Cloudflare's scripted challenge and Turnstile are reported unsolved. A stronger solver
  is a swap behind `IChallengeSolver`, not a rewrite - see
  [scnsrc-names-releases-it-does-not-serve-them.md](scnsrc-names-releases-it-does-not-serve-them.md).
- History records the source for a grab but not for a *failed* search, so a source that is
  asked and answers nothing looks the same as one never asked. Worth fixing when the
  resolver runs for real.
- Nothing has ever been grabbed on the owner's server: every wanted episode is at one search
  attempt with no history behind it, so the three configured sources answered nothing. Whether
  that is the cadences never firing (see the blocker above) or the sites themselves is the next
  thing to find out, and it is the last thing between here and a first download.
- `FolderLibrary` on the owner's server has a row joining the Series library to a folder with an
  empty path. Harmless to the plugin; the owner's to clean up.
- The sidebar draws nothing for a `library` or `video` mount
  (`Sidebar.vue` calls `pluginsInSection` for `Music`, `Dashboard` and `Settings` only). It
  matters less now the tab bar exists, but the entry point is still hard to find. Belongs in
  app-web.

## Working on the server

`scripts/deploy-to-server.ps1 -Build` needs the server stopped - a loaded plugin's assembly
is held open and the copy otherwise leaves the old build in place while looking like it
worked. `nomercy stop` and `nomercy start` over ssh do that. **Stopping it is the owner's
call, and whoever stops it finishes the restart** - this session left the server down for
minutes by stopping it and then carrying on with code.

The server is the owner's. Deploy the plugin; do not go investigating storage, queues or
anything else there without being asked.

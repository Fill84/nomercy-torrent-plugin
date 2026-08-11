# Handoff

Rewritten 11 August 2026. Everything below is committed locally; nothing is pushed.

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
`/libraries` where the app's own library pages live. A fix is committed on branch
`fix/library-plugin-prefix` in the media-server checkout: a private `SegmentFor` that maps
the one exception. It compiles; its tests could not be run here (sparse checkout), and the
server must be rebuilt before the URL changes.

## The eight pages

All built. `Pages.cs` holds the route table, the tab bar and the page frame; every view goes
through `Pages.Page` so no page can be missing its bar.

| route | page |
| --- | --- |
| `/` | Overview - is it working, what needs me |
| `/shows`, `/shows/:showId` | Per show: missing, running, arrived. Follow and unfollow |
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
- An empty `Library 3` on the owner's server, created by accident. Ask before removing it.
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

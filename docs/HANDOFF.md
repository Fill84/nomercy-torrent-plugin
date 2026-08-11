# Handoff

Rewritten 11 August 2026, after the first slice of the UI overhaul. Everything below is
committed locally; nothing is pushed.

## Where the plugin is

The loop works end to end in code and is tested: the library decides what is missing, a
feed announces what exists, a site is searched for that exact name, the engine downloads
it, the finished folder receives it, and a `VideoEncodeJob` is dispatched the way the
dashboard's own Add content dispatches one.

Verified on the real server: the plugin loads, its pages render, its endpoints answer, and
Search now really searched. **Never verified on the server:** a completed download, the
move, and the encode dispatch. Nothing has been downloaded yet. **Also not yet on the
server: everything in the slice below.**

Test count at handoff: 931 green. Working tree clean.

## Three things waiting on the owner

**A stop, a deploy, and a start.** Enabling the plugin in the dashboard registered its UI
and REST mounts but not its scheduled tasks - those are read when the host *loads* a
plugin, which never happened while it was disabled. Until a restart, no cadence runs. See
[[plugin-disabled-needs-restart-not-just-toggle]] in memory.

The deploy now has to happen in the middle of that, not before it:
`scripts/deploy-to-server.ps1 -Build` copies over ssh and **needs the server stopped**,
because a loaded plugin's assembly is held open and the copy silently leaves the old build
in place. While the plugin was disabled that did not matter; it is enabled now. So: stop,
deploy, start.

**Host access for `www.scnsrc.me`.** The plugin asked for it at runtime and the request is
pending. Until it is granted no search reaches the indexer. The overview page now says so
on screen rather than only in the server log.

**An empty `Library 3`** exists on the owner's server, created by accident during this
work. Ask before removing it.

## What the last slice did

`docs/superpowers/plans/2026-08-11-ui-overhaul-slice-1-pages.md` has the reasoning. In
short: the plugin now declares a `PluginRouteTable`, which is what makes a page reachable
at all - the server serves the table as the plugin's pages and the client registers a named
route for each. Undeclared pages fall back to a wildcard that only covers the legacy
`/plugins/{id}/…` mount, not the `/video/plugins/{id}/…` one this plugin sits behind, so
every tab past the two original routes would have hit the app's 404.

Seven pages, split out of the two that existed: Overview `/`, Downloads, Queue, History,
Sources, Skipped, Settings. One tab bar on every page, built from the table, with the
current page drawn as a badge because `PluginButton` draws every variant but `danger`
identically.

The video menu entry moved from `/downloads` to `/` and is labelled with the plugin's name.
The settings entry is unchanged.

## Next: the Shows page

The eighth page, and the only one the plan names that does not exist. It needs per-show
grouping - what is missing, what is running, what arrived, per show - which nothing builds
yet. Until it exists, the list of shows the plugin is leaving alone lives on Overview under
a heading that says what it is; it moves to Shows when Shows arrives.

Three things research settled earlier, so they are not re-litigated:

- app-web keys components on the plugin names (`PluginContainer`, `PluginText`), which is
  what `Ui.cs` sends. The old `NMCard` mismatch is fixed on their side.
- The whole vocabulary draws, including `PluginCard`, `PluginGrid`, `PluginDetail`,
  `PluginTable` and `PluginImage`. None of the seven pages uses them yet - they are all
  rows and lists, which is what the split needed. Shows is where a card earns its place.
- Posters are not available: `IPluginLibraryQuery` gives no poster path and an image URL is
  `/images/original{path}`. Build the show cards to read without pictures. The owner has
  said posters are not needed for now.

Stoney's `nm-component-ui` branch is behind this one - reference for naming only.

## The empty sidebar on the plugin's own page

Still open, still not this plugin's bug. In `nomercy-app-web`:

- `src/types/pluginVocabulary.ts:16` - the valid sections are `music`, `video`, `library`,
  `dashboard`, `settings`, `addon`.
- `src/store/plugins.ts:118` - `pluginsInSection(section)` returns the entries for any of
  them.
- `src/Layout/Desktop/components/Sidebar/Sidebar.vue:32-34` - the sidebar calls it for
  exactly three: `Music`, `Dashboard`, `Settings`.

So a `video` or `library` mount gets a route and an entry in the profile menu, and the
sidebar draws nothing for it. The fix belongs in app-web. It matters less than it did - the
tab bar means a plugin page is no longer a dead end - but the entry point is still hard to
find.

## Also still open

- The resolver is built and tested but has never run against a real site.
- `BrowserIdentitySolver` passes gates that check who is asking. It does not run
  JavaScript, so Cloudflare's scripted challenge and Turnstile are reported unsolved. A
  stronger solver is a swap behind `IChallengeSolver`, not a rewrite - see
  [scnsrc-names-releases-it-does-not-serve-them.md](scnsrc-names-releases-it-does-not-serve-them.md).
- Sources says how much each source has yielded, read off the history. History records the
  source for a grab; it does not record a *failed* search, so a source that is asked and
  answers nothing looks the same as one that is never asked. Worth fixing when the resolver
  runs for real.

## How this work is expected to go

One slice at a time, each with its own tests, each verified on the server before the next
begins. Twice in an earlier session a change was pushed further than it should have been
and had to be taken back - dropping unaired episodes instead of merely not searching for
them, and using FlareSolverr when the ask was for the plugin to have its own. Both were
caught by the owner, not by the tests. When the ask is ambiguous, ask.

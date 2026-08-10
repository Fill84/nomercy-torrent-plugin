# Handoff

Written 11 August 2026 at the end of a long session. Everything below is committed
locally; nothing is pushed.

## Where the plugin is

The loop works end to end in code and is tested: the library decides what is missing, a
feed announces what exists, a site is searched for that exact name, the engine downloads
it, the finished folder receives it, and a `VideoEncodeJob` is dispatched the way the
dashboard's own Add content dispatches one.

Verified on the real server: the plugin loads, its pages render, its endpoints answer, and
Search now really searched. **Never verified on the server:** a completed download, the
move, and the encode dispatch. Nothing has been downloaded yet.

Test count at handoff: 875 green. Working tree clean.

## Two things waiting on the owner

**A stop and start.** Enabling the plugin in the dashboard registered its UI and REST
mounts but not its scheduled tasks - those are read when the host *loads* a plugin, which
never happened while it was disabled. Until a restart, no cadence runs. See
[[plugin-disabled-needs-restart-not-just-toggle]] in memory. The restart also picks up
every build since, all of which are already on disk at
`$LOCALAPPDATA/NoMercy/plugins/NoMercy.Plugin.TorrentDownloader`.

**Host access for `www.scnsrc.me`.** The plugin asked for it at runtime and the request is
pending. Until it is granted no search reaches the indexer.

## The bug found last: an empty sidebar on the plugin's own page

Reported: clicking Downloads in the sidebar goes to
`/video/plugins/{id}/downloads` and the section navigation there is empty, where it stays
filled under `/libraries`.

Traced, and it is not the plugin's manifest. In `nomercy-app-web`:

- `src/types/pluginVocabulary.ts:16` - the valid sections are `music`, `video`, `library`,
  `dashboard`, `settings`, `addon`.
- `src/store/plugins.ts:118` - `pluginsInSection(section)` returns the entries for any of
  them.
- `src/Layout/Desktop/components/Sidebar/Sidebar.vue:32-34` - the sidebar calls it for
  exactly three: `Music`, `Dashboard`, `Settings`.

So a `video` or `library` mount gets a route and an entry in the profile menu, and the
sidebar draws nothing for it. The page is correctly attached to the app shell
(`pluginRoutes.ts`, "attached to the app shell rather than the top level, so a plugin page
keeps the sidebar"), which is why the chrome is there and only the section nav is bare.

The fix belongs in app-web - the sidebar should render plugin entries for `library` and
`video` as it already does for `music`. File it there. Changing this plugin's section to
`music` or `dashboard` would put a TV downloader in the wrong place to work around someone
else's gap.

## Next: the UI overhaul

The plan is decided and written up in [the-ui-overhaul.md](the-ui-overhaul.md): eight
separate pages behind one menu entry with tabs - Overview, Shows, Downloads, Queue,
History, Sources, Skipped, Settings.

Three things that research settled, so they are not re-litigated:

- app-web now keys components on the plugin names (`PluginContainer`, `PluginText`), which
  is what `Ui.cs` already sends. The old `NMCard` mismatch is fixed on their side.
- The whole vocabulary draws now, including `PluginCard`, `PluginGrid`, `PluginDetail`,
  `PluginTable` and `PluginImage`. The current single-column page exists because those did
  not render; that constraint is gone.
- Posters are not available: `IPluginLibraryQuery` gives no poster path and an image URL is
  `/images/original{path}`. Build the show cards to read without pictures. The owner has
  said posters are not needed for now.

Stoney's `nm-component-ui` branch is behind this one - reference for naming only.

## Also still open

- The resolver is built and tested but has never run against a real site.
- `BrowserIdentitySolver` passes gates that check who is asking. It does not run
  JavaScript, so Cloudflare's scripted challenge and Turnstile are reported unsolved. A
  stronger solver is a swap behind `IChallengeSolver`, not a rewrite - see
  [scnsrc-names-releases-it-does-not-serve-them.md](scnsrc-names-releases-it-does-not-serve-them.md).
- An empty `Library 3` exists on the owner's server, created by accident during this work.
  Ask before removing it.

## How this work is expected to go

One slice at a time, each with its own tests, each verified on the server before the next
begins. Twice in this session a change was pushed further than it should have been and had
to be taken back - dropping unaired episodes instead of merely not searching for them, and
using FlareSolverr when the ask was for the plugin to have its own. Both were caught by the
owner, not by the tests. When the ask is ambiguous, ask.

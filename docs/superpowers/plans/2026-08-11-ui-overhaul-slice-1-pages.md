# Slice 1 of the UI overhaul: the pages exist

Written 11 August 2026, continuing [the-ui-overhaul.md](../../the-ui-overhaul.md).

## Why a route table comes before any page

The plan is eight pages behind one menu entry with tabs. Tabs are navigation, and this
plugin has never declared where it can be navigated to.

`IUiPlugin.Routes` is a `PluginRouteTable` and defaults to empty. The server reads it in
`PluginUiController.Browse` and serves it as `pages`; app-web registers one named route per
page under the app shell. A page nobody declares falls back to a wildcard - and that
wildcard is `plugins/:pluginId/:pathMatch(.*)*` at the shell root, which covers
`/plugins/{id}/…` and not the `/video/plugins/{id}/…` prefix this plugin is actually mounted
behind.

So without the table, `PluginNavigation.To("/queue")` pushes a path nothing has registered
and the viewer gets the app's 404. Seven of the eight pages would be unreachable on the one
mount that matters.

Declaring it pays for two more things. Each route names the shell it wants and the server
stamps that onto the view (`View` only overrides a `Standard` layout, so a view that names
its own keeps it). And a tab built from `PluginRouteTable.GoTo` never writes the mount
prefix down, which is what lets the same tab bar work under `/video`, under `/dashboard`,
and on a television.

## The pages this slice declares

Seven. Shows is deliberately absent: it needs per-show grouping that does not exist yet, and
a Shows tab listing only the handful of shows the plugin is leaving alone would mislead
worse than no tab at all. Until it arrives, that list lives on Overview under a heading that
says what it is.

| route | name | page | comes from |
| --- | --- | --- | --- |
| `/` | overview | Is it working, what needs me | new, plus the summary line |
| `/downloads` | downloads | Only what is active, and a magnet by hand | today's Downloads page |
| `/queue` | queue | Every wanted episode, search-now per row | today's Wanted section |
| `/history` | history | What became of things | today's Recently section |
| `/sources` | sources | Feeds and sites: add, edit, remove | Settings' indexers + Downloads' add-a-source |
| `/skipped` | skipped | What is passed over and why | today's Skipped section |
| `/settings` | settings | Folders, schedules, quality, private trackers | today's Settings page, minus indexers |

The video mount moves from `/downloads` to `/`, labelled with the plugin's own name: one
entry that is the plugin, landing on the page that answers "is it working". The settings
mount stays on `/settings`, so the dashboard still has a direct way in. Two ways in, one
route table.

## Marking the current tab

`PluginButton` only distinguishes `danger` from everything else - every other variant draws
the same grey button. A variant therefore cannot say which tab you are on.

So the tab bar draws the current page as a `PluginBadge` (a pill, with real variants) and
every other page as a button that navigates. The one you are on looks like a label rather
than something to press, which is what it is.

## How the work is cut

1. `Pages.cs` - the route table, the tab bar, and the page frame every view is built through.
2. `Format.cs` - the formatting that was private to `DownloadsView` and is now shared by six
   pages. Moved, not rewritten.
3. One file per page, each a pure `Build` like the two views already are.
4. `TorrentDownloaderPlugin` declares `Routes`, and `GetViewAsync` resolves through the table
   instead of matching strings, loading only what the asked-for page needs.

Every page keeps the property the two existing views have: rows in, a view out, no I/O. That
is what lets each page be asserted whole without a server.

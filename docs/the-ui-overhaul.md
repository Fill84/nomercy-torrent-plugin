# The UI overhaul

Decided 11 August 2026, after checking what the dashboard can actually draw.

## What changed underneath

`nomercy-app-web` now keys its plugin components on the plugin names themselves -
`PluginContainer`, `PluginText`, `PluginList` - which is exactly what `Ui.cs` has been
sending. The mismatch that made every form arrive empty is fixed on their side.

More importantly the whole vocabulary draws now, not the nine tags this plugin restricted
itself to. `PluginCard`, `PluginGrid`, `PluginDetail`, `PluginTable`, `PluginImage` and
`PluginSpinner` are all mapped. The current page is a run of text rows because that was the
only thing that rendered; that constraint is gone.

Stoney's `nm-component-ui` branch was read as reference and is behind this one - it still
uses `PluginViews.Text` and still has the download-client screen removed here. Useful for
naming, not as a target.

## The pages

Eight, all separate, reached through one entry in the menu with tabs inside it. One entry
keeps the sidebar clean and keeps the plugin one thing rather than eight.

| page | what it answers |
| --- | --- |
| Overview | Is it working, what is happening now, what needs me |
| Shows | Per show: what is missing, what is running, what arrived. Follow and unfollow here |
| Downloads | Only what is active: progress, rate, ETA, peers, pause and cancel |
| Queue | Every wanted episode, with search-now per row |
| History | What happened: grabbed, imported, failed, skipped, with reason and time |
| Sources | Feeds and sites together: what each last did, how much it yielded, add and remove |
| Skipped | What is being passed over, why, how long left, and allow again |
| Settings | Folders, schedules, quality profile, private trackers |

## No posters yet

Checked rather than assumed: `IPluginLibraryQuery` hands a plugin the show's id, title,
year, library and folder. There is no poster path, and app-web builds an image URL as
`/images/original{path}` where the path comes from the metadata provider - so an id alone
cannot be turned into one.

The Shows page is therefore built to read without pictures and to take them when they
arrive: a card whose image slot is empty must look deliberate, not broken. A request for
the poster path on `PluginLibraryShow` goes to media-server separately.

## How

One page per slice, each with its own tests, each verified on the server before the next
begins. The style follows the dashboard's own components rather than anything invented
here - this is the surface the owner judges the plugin by, and a plugin that looks like a
plugin is one they stop opening.

# This is a NoMercy plugin

Not a service, not a sidecar, not a script. It is loaded into the NoMercy media server's process by
the server's plugin manager, and everything it does goes through the plugin contract.

## Identity

| | |
| --- | --- |
| Id | `1SBQT26FHF98EBRPYVRGD92CZF` — **unchanged from 0.3.4**; it is the plugin's identity on every server that has it installed |
| Name | Torrent Downloader |
| Version | `0.4.0` |
| Target ABI | `10.0` — the server's own `PluginAbi.Current` on `dev`; see below |
| Assembly | `NoMercy.Plugin.TorrentDownloader.dll` |

`AbiVerificationStage` is enforced and accepts a manifest only when
`requested.Major == Current.Major && requested.Minor <= Current.Minor`. A manifest asking for `10.1`
is therefore **refused outright** by a server whose `PluginAbi.Current` is `10.0`. This document said
`10.1` until S0-02 read the server; the reference dump's file name still carries that number.
`ManifestTests` asks `PluginAbi.IsCompatible` rather than a number written down, so the manifest and
the contract this build compiles against cannot drift apart again.

The id staying the same is what makes 0.4.0 an upgrade rather than a second plugin: the server
keeps the same data folder, the same grants and the same settings location.

## What it implements

| Interface | What the server does with it |
| --- | --- |
| `IPlugin` | loads it, calls `Initialize(IPluginContext)`, disposes it on shutdown |
| `IScheduledTaskPlugin` | registers its one job and calls `ExecuteAsync(jobName, ct)` |
| `IUiPlugin` | asks it for pages via `GetViewAsync(PluginViewRequest, ct)` and mounts its nav entries |

Plus its own REST endpoints through `NoMercy.Plugins.Mvc`, and live pushes through
`IPluginHubContext`.

## The four cadences, and the plugin's own clock

| Cadence | Default cron | Does |
| --- | --- | --- |
| `transfers` | `* * * * *` | watch what is downloading; stage and dispatch what finished |
| `feed` | `*/15 * * * *` | read every feed into the name pool |
| `search` | `0 */6 * * *` | resolve names for missing episodes, find copies, grab |
| `maintenance` | `0 4 * * *` | re-derive the missing list, prune old refusals, clear duplicate grab rows |

**Only one job is registered with the host: `transfers`, every minute (S12-05).** The host reads
`IScheduledTaskPlugin.Jobs` only when the plugin is installed, hot-swapped or enabled —
`PluginCronRegistrar.RegisterPlugin` re-reads it, but only those three call it, and there is no
capability for a plugin to ask for its own re-registration (media-server #53). A saved cadence
therefore cannot take effect by changing a registration: it takes effect because `Hosting/Clock.cs`
is asked, fresh, on every transfers tick, which of `feed`, `search` and `maintenance` are due —
judged by the owner's saved interval and a `cadences` table holding when each last finished. A
cadence with no row has never run and is due at once, which is the right answer on a fresh install.
When #53 is closed, the plugin can hand the timing back to the host and this clock goes.

**A tick under one of the three retired job names is still accepted.** A host that has not yet
re-read `Jobs` after this upgrade is still holding its previous four-job registration, each still
firing on its own old cadence, and `ExecuteAsync` still runs that one pass for that one name — it is
only the tick under `transfers` that also asks the clock.

**Every piece of periodic housekeeping is in `maintenance`.** Not because it is tidy, but because
housekeeping spread across the cadence that happened to be running when somebody needed it is
housekeeping nobody can find. `search` re-derives the missing list of its own accord as well — a
cycle needs a fresh one and must not wait for four in the morning — and that is the only overlap.

**A start settles once, whichever cadence ticks first.** What the library holds is derived rather
than stored, so a plugin that only re-derived it on its six-hourly cycle carried whatever the last
run left behind. On 24 August 2026 that was shows a broken build had put there that the owner does
not have. A restart settles within the minute instead. It runs the maintenance work, so no cadence
has a first tick unlike its others.

**A saved cadence takes effect on the very next tick, not on the next restart.** That is the whole
point of the clock above: `feed`, `search` and `maintenance` are judged against the owner's current
setting every single time, never against a schedule fixed when the server started.

## What the server gives it

Everything comes from `IPluginContext`. The plugin holds no database connection to the server, no
EF context, and no reference to the server's assemblies.

| Member | Used for |
| --- | --- |
| `Library` | which shows and episodes exist — see `docs/02-library.md` |
| `DataFolderPath` | the plugin's own SQLite database, its browser, its resume data |
| `Secrets` | private tracker passkeys and indexer API keys |
| `Grants` | asking the owner for network access to hosts the owner configured |
| `Hub` | pushing the live snapshot to every open page |
| `Logger` | the server's log |
| `Services` | reaching the encode dispatcher by name — see `docs/09-host-contract.md` |
| `HttpClient` | outbound requests |

## Where it lives on disk

```
%LOCALAPPDATA%\NoMercy\plugins\NoMercy.Plugin.TorrentDownloader\   the assemblies, plugin.json, sources.json
%LOCALAPPDATA%\NoMercy\plugins\data\1SBQT26FHF98EBRPYVRGD92CZF\    settings, SQLite, browser, resume data
```

`sources.json` is read from **the assembly's own folder**. Not `AppContext.BaseDirectory` — a
plugin is loaded into the server's process, so that property names the server's folder, the
catalogue is silently never found, and the plugin runs on a compiled-in fallback while looking
perfectly healthy. That happened, for a day.

## Manifest

`plugin.json` ships beside the assembly:

```jsonc
{
  "id": "1SBQT26FHF98EBRPYVRGD92CZF",
  "name": "Torrent Downloader",
  // Required by PluginManifest, so a manifest without one fails to deserialise.
  "description": "Downloads every episode missing from a TV or anime library and hands it to the encoder.",
  "version": "0.4.0",
  "targetAbi": "10.0",
  "assembly": "NoMercy.Plugin.TorrentDownloader.dll",
  "autoEnabled": true,
  "capabilities": {
    "hooks": ["scheduledTask", "ui"],
    "rest": true,
    "ws": false,
    "network": { "hosts": [ /* every host in sources.json */ ] },
    "ui": { "mounts": [
      { "section": "dashboard", "route": "/",         "label": "Torrent Downloader", "icon": "download" },
      { "section": "settings",  "route": "/settings", "label": "Torrent Downloader", "icon": "download" }
    ] }
  }
}
```

Three things a test must keep true, because each has broken before:

1. `plugin.json`'s version equals `PluginIdentity.Version`. They carry it independently, and a
   server reporting a version it is not running is worse than no version at all.
2. The manifest's UI mounts equal `IUiPlugin.NavEntries`, entry for entry.
3. Every host in `sources.json` appears in `capabilities.network.hosts`, and nothing else does.

## Building against the contract

`NoMercy.Plugins.Abstractions` is not on nuget.org. `scripts/fetch-abstractions.*` clones the media
server (sparse, shallow, branch **`master`**) and packs the contract locally into `_nupkgs/`.

**`master`, never `dev`.** `dev` carries a fixed `0.1.404` that never moves, so packing from it gives
a contract older than the one released servers ship — and the build then fails with a `CS0246` naming
a type, which reads like a missing `using` and is really a server too old. This plugin is installed
on servers running a release, so it compiles against what those servers carry. After
repacking the same version number, clear that package's NuGet cache entry or the old one is used
and nothing says so.

The full exported surface is in `docs/reference/plugin-abi-0.1.479.txt`.

## Deploying

**The server must be stopped first.** A loaded plugin's assembly is held open, so the copy fails
and the old build stays in place — which looks exactly like a deploy that worked and changed
nothing. `scripts/deploy-to-server.ps1` compares every file's hash afterwards, which is the only
way to tell those two apart.

Files that travel: the plugin assemblies, `deps.json`, `plugin.json`, **`sources.json`**, and the
browser driver's assemblies. `Newtonsoft.Json`, `Ulid` and every `NoMercy.*` are deliberately
absent — they are in `PluginHostOptions.DefaultSharedAssemblies`, so the load context returns null
for them and the host's copy is used. Shipping a second copy means a second assembly identity for
`[JsonProperty]`, which is the bug that list exists to prevent.

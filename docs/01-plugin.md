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
| `IScheduledTaskPlugin` | registers its four cron jobs and calls `ExecuteAsync(jobName, ct)` |
| `IUiPlugin` | asks it for pages via `GetViewAsync(PluginViewRequest, ct)` and mounts its nav entries |

Plus its own REST endpoints through `NoMercy.Plugins.Mvc`, and live pushes through
`IPluginHubContext`.

## The four cadences

| Job | Default cron | Does |
| --- | --- | --- |
| `transfers` | `* * * * *` | watch what is downloading; stage and dispatch what finished |
| `feed` | `*/15 * * * *` | read every feed into the name pool |
| `search` | `0 */6 * * *` | resolve names for missing episodes, find copies, grab |
| `maintenance` | `0 4 * * *` | re-derive the missing list from the library, prune, re-verify |

**Cadences are registered once, when the server starts.** A plugin loads a minute or two after the
server, and changing a cron at runtime re-registers nothing — only a server restart applies a new
schedule. This is the server's behaviour, not a bug to chase.

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
      { "section": "library",  "route": "/",         "label": "Torrent Downloader", "icon": "download" },
      { "section": "settings", "route": "/settings", "label": "Torrent Downloader", "icon": "download" }
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
server (sparse, shallow, branch **`dev`**) and packs the contract locally into `_nupkgs/`. After
repacking the same version number, clear that package's NuGet cache entry or the old one is used
and nothing says so.

The full exported surface is in `docs/reference/plugin-abi-10.1.txt`.

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

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

Plus its own REST endpoints through `NoMercy.PluginSdk.Mvc`, and live pushes through
`IPluginHubContext`.

## One cycle, driven by what finishes

Decided with the owner on 13 September 2026. The full design and the evidence for every decision in it
is `docs/plan/DESIGN-2026-09-13-one-cycle-driven-by-events.md`.

**Contract 12 took the server's events away** (22 September 2026), and this section is what is left
once they are gone. The chain is unchanged and still driven by what finishes; what the server used to
say on a bus is now either asked of it or covered by the cadence. Each place is named below, and
`docs/09-host-contract.md` § What contract 12 took away has the whole table.

There are no cadences for the work. There is **one cycle**, and each step is started by the last one
finishing:

```
feed → search → the torrent client → a download finishes → staged → encode dispatched
     → the server says the encode ended → nothing left in hand → maintenance
```

**Two things start a cycle**, both through one door (`Trigger`):

| Trigger | |
| --- | --- |
| The **Run** button | the owner |
| The owner's cadence | one setting, **hourly** by default, and **never more often than every 15 minutes** (`docs/specs/release-names.md`) |

A library scan was the third until contract 12. Nothing on 12 says a scan finished — `Library.Watch`
is refused by name on an in-process plugin — so the run interval covers it (`docs/specs/run.md`).

**A trigger during an open cycle is added to it**, never run beside it and never dropped. What the
open cycle has already taken is written down as it takes it, so the feed and search it runs again
exclude those by themselves.

**A cycle is open until nothing is left in hand** — nothing the torrent client holds, nothing staged,
nothing waiting on an encode — and then maintenance runs and the cycle closes. Maintenance last,
because it sweeps download folders no grab answers for. A grab written down but never started does
not hold a cycle open.

**Nothing in the chain is a timer.** A finished download is `BittorrentEngine.Completed`, raised where
the last piece verifies. A torrent the client gives up on — its metadata never came, it stalled, it
holds no video, it will not fit — is `BittorrentEngine.GaveUp`, and the pass it starts is what fails
the grab and takes the torrent out of the client; without it a dropped torrent was held for ever and
the cycle with it. What became of an encode is asked of `Context.Jobs` by the id `EncodeAsync` handed
back, kept with the grab, on every transfers pass — the events that said so are gone with contract 12,
and a pass runs every minute while a grab waits on one. Every pass asks whether the cycle can close.

**The library decides when an encode has arrived; no clock decides it is lost.** The server says
nothing about a job the owner takes out of the queue by hand, or one that ends with nothing to encode,
so a grab waiting on one of those waits until a pass finds its episode in the library — which is why a
grab waiting on an encode is passed over every minute rather than only when something else happens.
Or until the owner cancels it on the Downloads page. An encode is never asked for a second time, a restart
included: the server's queue outlives a restart and the job says what became of it when it runs. The
owner's ruling of 14 September 2026; it replaced a six-hour give-up that put the episode back to
missing and downloaded it again.

**A failed attempt closes nothing either.** The server's queue tries a failed job again — up to three
attempts, a stop not counted — so a grab is failed only once `Context.Jobs` says the server has truly
given the job up, with the reason the server wrote. The reason is said once, on the History page and
beside "encoding" on the Downloads row, and the grab keeps its episodes and its staged file for the
next attempt. The same ruling, the same day; on contract 12 the answer comes from the queue's own
tables rather than from an event, which is the better of the two — an event fired on a failure the
server was about to retry.

**The owner's cadence is kept by the plugin's own clock**, set to the moment the next cycle is due
rather than woken to ask whether one is, and wound again when a cycle closes. A saved cadence takes
effect at once: the host reads `IScheduledTaskPlugin.Jobs` only when a plugin is installed, hot-swapped
or enabled, so a schedule declared to it could never follow a change.

**The host is still told to tick this plugin, hourly, and that tick starts nothing.** The contract has
no way to decline a schedule — a plugin whose `Jobs` is empty is registered under its single
`CronExpression` instead — so one job, `cycle`, is declared, and all it does is make sure the clock is
wound. The four retired names — `transfers`, `feed`, `search`, `maintenance` — are still answered to
and start nothing, because the host removes a plugin's jobs by the names the loaded instance declares
and an upgrade can leave the old four registered.

**A start settles once, whichever asks first.** What the library holds is derived rather than stored,
so a plugin that only re-derived it on a schedule carried whatever the last run left behind — on
24 August 2026, shows a broken build had put there that the owner does not have. The first thing that
asks anything of a started plugin runs the maintenance work once.

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
| `Encoder` | asking for an encode; handed over only because `plugin.json` names the `encoder` hook |
| `Jobs` | what became of an encode, by the job id `EncodeAsync` handed back |
| `Server` | `GrantedPaths`, the places a folder may be chosen from |
| `HttpClient` | outbound requests |

`Services` — the server's own container — was here until contract 12 took it away on 22 September
2026. Nothing reaches into the server outside this contract any more; see `docs/09-host-contract.md`.

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
  "version": "0.7.1",
  "targetAbi": "12.0",
  "assembly": "NoMercy.Plugin.TorrentDownloader.dll",
  "autoEnabled": true,
  "capabilities": {
    // "encoder" since 0.7.0: on contract 12 the encoder is handed over only to a manifest that names
    // it, and naming a hook the owner's consent did not widens the manifest — the server then loads
    // the update disabled until they approve it once more.
    "hooks": ["scheduledTask", "ui", "encoder"],
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

The contract is `NoMercy.PluginSdk.Abstractions` and `NoMercy.PluginSdk.Mvc`, version `12.*` — the major
pinned, the minor floating, as the server's own plugin template does. The server publishes them to
nuget.org with a release; until one carrying the renamed contract has shipped, they are not there, and
`scripts/fetch-abstractions.*` clones the media server (sparse, shallow, branch **`dev`**) and packs them
locally into `_nupkgs/`.

**`dev`, since 22 September 2026.** The contract carries its own `<PluginPackageVersion>` (12.0.0 at the
time of writing) rather than the server's version, so the reason these scripts refused `dev` — a
`<Version>` frozen at `0.1.404` that NuGet took for "already have it" — is gone. `master` is where the
contract is not: it sits on a release from August carrying ABI 11, and the servers the owner and Stoney
run come from `dev` and refuse anything under 12. After repacking the same version number, the script
clears that package's NuGet cache entry itself, or the old one is used and nothing says so.

`NoMercy.Events` and `NoMercy.Design` ship as assemblies inside the Abstractions package and are not
packed on their own. `NoMercy.PluginSdk.Analyzers` is not referenced: it bans the sockets, files and
processes an in-process plugin still uses, and taking those behind the facades is work not yet done.

The full exported surface of the previous contract is in `docs/reference/plugin-abi-0.1.479.txt`; what
changed for this plugin at 12 is in `docs/09-host-contract.md`.

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

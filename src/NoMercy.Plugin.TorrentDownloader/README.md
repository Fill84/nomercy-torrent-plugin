# NoMercy.Plugin.TorrentDownloader

A plugin for the [NoMercy MediaServer](https://github.com/NoMercy-Entertainment/nomercy-media-server)
that keeps a TV library complete by downloading missing episodes over BitTorrent.

This is the README that ships **inside the plugin package**. The repository's own README covers
building and contributing.

> **This build loads but does not download.** The release parsing, matching and indexer layers are
> built and tested, and the plugin loads and registers its jobs. The download database, the torrent
> clients and the loop connecting them are not built yet. The settings page is **read-only** until
> the plugin's REST surface lands, so there is nothing to configure. Install it to confirm it loads
> and reads your library — not to fetch episodes.

## Install

1. Extract the package into `<server>/plugins/` so you have
   `<server>/plugins/NoMercy.Plugin.TorrentDownloader/plugin.json`.
2. Restart the server.
3. Enable the plugin in the dashboard, and grant consent when prompted.

It installs disabled (`autoEnabled: false`) and asks before it runs, which for something that will
eventually write to your library and reach the internet is the right default.

## What ships in the package

| File | |
| --- | --- |
| `plugin.json` | the manifest the server reads at start-up |
| `NoMercy.Plugin.TorrentDownloader.dll` | the plugin — the only assembly that touches the host |
| `NoMercy.Plugin.TorrentDownloader.Core.dll` | the domain engine: parsing, matching, profiles, indexers |
| `NoMercy.Plugin.TorrentDownloader.deps.json` | so the load context resolves `Core` |
| `README.md`, `LICENSE` | this file, and MIT |

`NoMercy.Plugins.Abstractions.dll` and `NoMercy.Events.dll` are **deliberately absent.** The server
owns both and they live in its shared-assembly set; a copy sitting beside the plugin would give the
load context two incompatible identities of the same types, and the failure surfaces as an unrelated
cast error far from its cause. CI asserts they never ship.

## What it declares, and why

| | |
| --- | --- |
| `scheduledTask` | four jobs on separate cadences — `transfers`, `feed`, `search`, `maintenance` — each visible and individually timeable in the server's job list |
| `ui` | one page under plugin settings |
| `rest` / `ws` | **not declared.** They arrive with the REST surface; declaring a capability the plugin does not exercise asks for power it does not use |
| `network.hosts` | **empty by design.** Your indexer and client addresses are configuration a manifest written at package time cannot know, so the plugin asks for each host at runtime through the grant system and the dashboard shows you the request |
| `libraryWrite` | **not declared.** It arrives with quality upgrades that replace a file. It is an elevated capability, so declaring it early would prompt you to approve deleting media for a feature that does not exist |

## How it treats your credentials

Indexer API keys and torrent-client passwords go to the server's protected secret store, never to
plugin configuration — that is whole-object JSON on disk, so anything written there would sit in
plaintext. The types handed to the configuration store have nowhere to put a secret, so this is a
property of the code's shape rather than a rule someone has to remember.

The settings page never shows a stored secret back to you. A saved credential renders as an empty
field whose placeholder says one is stored, so "never set" and "set, not shown" are distinguishable.

## Scheduled jobs

| Job | Default | What it will do |
| --- | --- | --- |
| `transfers` | every minute | poll clients for progress, detect completions and stalls |
| `feed` | every 15 minutes | read RSS and scene feeds |
| `search` | every 6 hours | search indexers for wanted episodes |
| `maintenance` | daily at 04:00 | prune caches, rotate the activity log, expire blacklist entries |

In this build each job logs what it would do and returns.

## Requirements

NoMercy MediaServer with the plugin ABI **10.0** contract, on .NET 10.

## Licence

MIT. This plugin is the author's own work and is not part of NoMercy MediaServer.

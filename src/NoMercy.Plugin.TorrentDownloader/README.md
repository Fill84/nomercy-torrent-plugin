# NoMercy.Plugin.TorrentDownloader

A plugin for the [NoMercy MediaServer](https://github.com/NoMercy-Entertainment/nomercy-media-server)
that keeps a TV library complete by downloading missing episodes over BitTorrent.

This is the README that ships **inside the plugin package**. The repository's own README covers
building and contributing.

It downloads by itself. There is no qBittorrent to install and nothing to point it at: the
BitTorrent client is in `Core.dll` — peer wire, forced encryption, trackers over HTTP and UDP, DHT,
magnet links, resume across restarts. Configure an indexer and it will start filling gaps.

## Install

1. Extract the package into `<server>/plugins/` so you have
   `<server>/plugins/NoMercy.Plugin.TorrentDownloader/plugin.json`.
2. Restart the server.
3. Enable the plugin in the dashboard, and grant consent when prompted.
4. Add an indexer on its settings page. Until you do, it searches nothing.

It installs **disabled** and asks first. That is not `autoEnabled` doing the work — declaring
`rest` makes the plugin non-baseline, so the server holds it at Disabled until you consent, whatever
the manifest says. `autoEnabled: true` is what lets it come back by itself after a restart once you
have consented; `false` would make you re-enable it every time the server starts.

## What it downloads, and what it leaves alone

**A show with at least one episode already on the server is one you started, so the rest of it is
worth completing. A show with nothing is one nobody asked for.** The library lists everything the
metadata provider knows about; it is not a statement of intent. Without that rule a first run on a
real library queued 1973 episodes, nearly all of them shows their owner had never held a file of.

The downloads page lists the shows being left alone with a **Follow** button beside each, which is
how you start one deliberately. Specials (season 0) are excluded unless you turn them on.

Nothing is ever uploaded unless you add a private tracker and switch seeding on for it. Every other
torrent is treated as public, and public never seeds.

## What ships in the package

| File | |
| --- | --- |
| `plugin.json` | the manifest the server reads at start-up |
| `NoMercy.Plugin.TorrentDownloader.dll` | the plugin — the only assembly that touches the host |
| `NoMercy.Plugin.TorrentDownloader.Core.dll` | the engine and domain: BitTorrent, parsing, matching, profiles, indexers |
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
| `ui` | two pages: settings, and a downloads page beside films and shows |
| `rest` | **declared.** The settings page and the follow buttons save through it, reachable only for this plugin's own controller |
| `ws` | **not declared.** No hub handler exists; the downloads page asks the client to re-fetch every thirty seconds instead |
| `network.hosts` | **empty by design.** Your indexer and tracker addresses are configuration a manifest written at package time cannot know, so the plugin asks for each host at runtime through the grant system and the dashboard shows you the request |
| `libraryWrite` | **not declared.** Finished downloads go to the intake folder and the server's own watcher imports them, so the plugin never writes into a library. It arrives with quality upgrades that replace a file, and declaring it early would prompt you to approve deleting media for a feature that does not exist |

## How it treats your credentials

Indexer API keys go to the server's protected secret store, never to plugin configuration — that is
whole-object JSON on disk, so anything written there would sit in plaintext. The types handed to the
configuration store have nowhere to put a secret, so this is a property of the code's shape rather
than a rule someone has to remember.

A private tracker's **announce URL is itself a secret**, not a field with one in it: the passkey in
it is the account. It is stored the same way and never rendered back.

The settings page never shows a stored secret. A saved credential renders as an empty field whose
placeholder says one is stored, so "never set" and "set, not shown" are distinguishable. Leaving it
blank keeps what is stored.

## Scheduled jobs

| Job | Default | What it does |
| --- | --- | --- |
| `transfers` | every minute | poll the engine, record progress, hand finished downloads to the intake, blacklist and retry failures |
| `feed` | every 15 minutes | work out what the library is missing across the shows it follows |
| `search` | every 6 hours | search indexers for wanted episodes and grab what passes the profile |
| `maintenance` | daily at 04:00 | re-read the library, so a file added or deleted by hand is noticed |

## Where files go

Two folders on the settings page, both defaulting to the plugin's own data folder:

- **Incomplete downloads** — where the engine writes while downloading.
- **Intake** — where a finished download is moved for the server to import.

They are typed paths today. The server's Storage system is not reachable from a plugin, so there is
no folder picker; the plugin's own docs record what would be needed for one.

## Requirements

NoMercy MediaServer with the plugin ABI **10.0** contract, on .NET 10.

## Licence

MIT. This plugin is the author's own work and is not part of NoMercy MediaServer.

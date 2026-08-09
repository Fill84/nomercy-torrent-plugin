# Settled: why every form on this plugin's pages submitted nothing

**Filed upstream as:** `nomercy-media-server#27`
**Closed as misdiagnosed:** `nomercy-app-web#19` (mine, wrong layer)
**Settled on:** beast-unit, server `0.1.470`, 2026-08-09

## The question this closes

`docs/inbox/2026-08-08-from-yt-plugin-form-body-contradiction.md` recorded two claims that could
not both be true. This repository said a `PluginForm` posts its fields. The radio plugin said the
posted body was `{}` every time, having tried four shapes.

**The radio plugin was right, and it also found why.** That inbox note is deleted rather than
updated: it existed to hold an open question, and the question is answered.

## The answer

`PluginComponentType` in the contract maps eight components onto one design-system name:

```csharp
Container = List = Row = Grid = Card = Detail = Form = Table = "NMCard"
```

The clients key plugin components by their own names — `PluginForm`, `PluginList`, `PluginText` —
and resolve a design-system name as a design-system component first. So everything this plugin
sent was drawn as a card, and `PluginForm`, the real `<form>` that collects and posts its fields,
was never reached.

Proved rather than argued: an indexer typed into the settings page and saved left
`"Name": "New Indexer 1", "Url": ""` in `config.json`, while the page said it had saved. After
sending the client's own names, the same edit wrote `"Name": "SceneSource"` with its URL, and the
plugin immediately asked for host access to `www.scnsrc.me` — which only happens once it has read
the indexer back.

## What this plugin does about it

`src/NoMercy.Plugin.TorrentDownloader/Views/Ui.cs`, taken from the radio plugin's file of the same
name so the two agree. Both hardcode the client's names, read off its own component map rather
than guessed. If the contract is corrected upstream, each plugin changes in one file.

## What was wrong with my first reading, and why it is recorded

I filed `nomercy-app-web#19` claiming `NMSelect` and `NMInput` ignore a plugin-supplied `value`
because they bind `modelValue`. That is true of those components and irrelevant here: this
plugin's fields were never `NMSelect` and `NMInput`, they were the contents of a card. Closing it
mattered as much as filing the right one — a plausible issue pointing at the wrong layer costs the
next person the same day it cost me.

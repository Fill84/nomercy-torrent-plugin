# The goal

**Every episode that is missing from a TV or anime library and has already aired gets downloaded
and handed to the encoder, without anybody at the keyboard — and the owner can see it happening.**

That is the whole plugin. Anything that does not serve it is not built.

## What "missing" means

The owner does not follow or subscribe to anything. They have libraries of type `tv` and `anime`,
full of shows with episodes, and they switch on the shows they want on the overview page. Any episode
of a show switched on, with a quality of its own or its library's, that has no video file and whose air
date has passed is missing, and this plugin fetches it (`docs/specs/show-list.md`).

**Backwards as well as forwards.** An episode that aired two years ago and was never downloaded is
missing in exactly the same way as one that aired last night. A show that has ended is not skipped
— it is precisely the kind of show with gaps to fill.

## The chain

```
1. Libraries   read every library of type tv or anime
2. Shows       every show in them switched on with a quality (its own or its library's), every episode
3. Missing     no video file, air date in the past; season 0 only with specials on
4. Names       read the latest feed of every name source — PreDB, srrDB, PreDB.net, SceneSource —
               on every run; an episode no feed named is looked up in their search as Show SxxEyy;
               keep the release NAMES of one episode that meet the show's settings — quality,
               codec, every must, no forbidden — most wishes first
5. Find        the first-choice indexers one after another, then every other enabled indexer:
               every name letter for letter, then without its punctuation; merge the matches by
               info hash so one torrent carries every tracker; the one the most indexers list wins
6. Download    the plugin's own BitTorrent client takes it
7. Dispatch    stage the video and queue an encode job with the right library, folder and media id
```

Step 7 is where the plugin stops. Putting the encoded file into the library is the server's work.

## Two rules that shape everything

**A name is not a copy.** A scene database says a release exists and what it is called. It has no
seeders, no tracker and no magnet. The decision *what to download* is made on the name; the
question *who has it* is put to the indexers afterwards. Asking a name how many seeders it has is
what stopped 0.3.4 downloading anything at all.

**An indexer is asked the full release name first, letter for letter.**
`Silo.S03E06.1080p.WEB.H264-CAKES`, dots and dash as the source wrote it — then the same name without
its punctuation, and nothing else. A row whose title is that release *is* that release; a row that
looks similar is a guess. The owner's rule of 11 September 2026. Until 15 September 2026 a site with
nothing for either went down a ladder to `Silo S03E06 1080p`; since then the plugin builds no search
term of its own (`docs/specs/indexer-search.md`).

## What the owner configures

The five public name sources and eleven public indexers ship with the plugin and are not
configurable.
On top of them the owner may add their own indexers and their own private trackers, because those
cannot be shipped.

They also configure: the two folders, the run interval, the torrent client's limits, and on the
overview which shows are switched on with each show's and each library's settings. Everything else
has a working default; a show nobody switched on is off.

## Non-goals

- Importing into the library. The plugin dispatches an encode job; the server does the rest.
- Managing the encoder, the queue or the library schema.
- Writing anything but video files into a library folder.
- Opening a browser window on anybody's desktop, on any platform.

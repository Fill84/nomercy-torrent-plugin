# The goal

**Every episode that is missing from a TV or anime library and has already aired gets downloaded
and handed to the encoder, without anybody at the keyboard — and the owner can see it happening.**

That is the whole plugin. Anything that does not serve it is not built.

## What "missing" means

The owner does not follow, subscribe to or select anything. They have libraries of type `tv` and
`anime`, full of shows with episodes. Any episode of any of those shows that has no video file and
whose air date has passed is missing, and this plugin fetches it.

**Backwards as well as forwards.** An episode that aired two years ago and was never downloaded is
missing in exactly the same way as one that aired last night. A show that has ended is not skipped
— it is precisely the kind of show with gaps to fill.

## The chain

```
1. Libraries   read every library of type tv or anime
2. Shows       every show in them, every episode
3. Missing     no video file, air date in the past
4. Names       read every feed and scene database; pick the release NAME that already meets
               the profile — resolution, codec, language, group
5. Find        search that full release name on every indexer; merge the matches by info hash
               so one torrent carries every tracker
6. Download    the plugin's own BitTorrent client takes it
7. Dispatch    stage the video and queue an encode job with the right library, folder and media id
```

Step 7 is where the plugin stops. Putting the encoded file into the library is the server's work.

## Two rules that shape everything

**A name is not a copy.** A scene database says a release exists and what it is called. It has no
seeders, no tracker and no magnet. The decision *what to download* is made on the name; the
question *who has it* is put to the indexers afterwards. Asking a name how many seeders it has is
what stopped 0.3.4 downloading anything at all.

**An indexer is asked the full release name.** `Silo.S03E06.1080p.WEB.H264-CAKES`, never
`Silo S03E06`. A row whose title is that release *is* that release; a row that looks similar is a
guess.

## What the owner configures

The fifteen public sources ship with the plugin and are not configurable. On top of them the
owner may add their own indexers and their own private trackers, because those cannot be shipped.

They also configure: the two folders, four cron expressions, the quality profile, and the torrent
client's limits. Everything has a working default.

## Non-goals

- Importing into the library. The plugin dispatches an encode job; the server does the rest.
- Managing the encoder, the queue or the library schema.
- Writing anything but video files into a library folder.
- Opening a browser window on anybody's desktop, on any platform.

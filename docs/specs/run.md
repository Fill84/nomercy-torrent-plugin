# A run

## What starts a run

- A run starts when the owner presses Run and when the run interval comes round.
- A start that arrives while a run is going is added to that run.
- A library scan no longer starts a run. It did until plugin contract 12 (22 September 2026), which gives
  a plugin no way to hear the server finish one: the bus is gone from the context, and the facade made for
  it is refused on a plugin running in the server's own process. The run interval covers it.

## The order of a run

- A run first works out the episodes being searched for: the aired episodes without a video file of
  the shows and anime that are switched on with a quality, their own or their library's, and the
  episodes to be replaced.
- A grab records whether its release is a name a name source gave. An episode of a show whose latest
  delivery by the plugin is a release no name source gave is to be replaced, when that delivery is of that
  one episode or is a season pack. A double episode or a run of episodes is never to be replaced: one file
  holds both, and no name source names such a release. An anime's episode is never to be replaced, and
  neither is an episode the owner put in the library or a torrent the owner added by hand. A delivery
  whose replacement is open or done marks nothing more: a second replacement would recycle what the
  first one's new encode wrote. One that failed or was lost makes way for another.
- A delivery recorded without that is checked once, after the run's own search, against the search of
  every name source for its episode, the same search the run asks; a release none of them names makes
  its episode to be replaced, and the next run searches for it. At most ten are checked in one run, the
  rest on the runs after; a run for one episode the owner asked for checks none. A delivery a name
  source did not answer about is checked again by a later run.
- An episode to be replaced is searched for by release name like an episode without a file, and only
  by release name. It is not missing and is not counted as missing, and it is never answered for by a
  season pack.
- The episodes are worked through in order of show, season and episode.
- A run reads the feeds of all name sources at the same time before it works on any episode.
- A feed release name is taken for an episode when its show and its season and episode number are
  those of an episode being searched for. Every other feed release name is left.
- The episodes are worked on one at a time, and an episode's winning torrent is offered to the
  download client before the next episode is worked on.
- When the run reaches the first episode of a season with several gaps, it looks for that season's pack
  first (`release-names.md` § Season packs): the feed season packs taken for it, or else the search of
  every name source for the show and the season, then the indexers as for an episode's release names.
- A season pack that gives a torrent is offered to the download client before the next episode is worked
  on, and the season's other episodes being searched for are not worked on again in that run.
- When no season pack gives a torrent, the episode the run reached and every later episode of that season
  are worked on one by one.
- For an episode, the release names are the feed release names taken for it. An episode with no feed
  release name is looked up in the search of every name source.
- For an episode, the indexers are asked for one wish group at a time, starting with the group
  carrying the most wishes.
- The first-choice indexers are asked one after another in their order, and then every other enabled
  indexer at the same time.
- A torrent found first is one found on an indexer earlier in that order, and among the other
  indexers one earlier in the source catalogue.
- A question already asked of an indexer during a run is not asked of it again in that run.
- A show's episode with no release name that meets its show's settings is not searched for on any
  indexer, and a show's episode for which no wish group produces a torrent is not downloaded
  (`release-names.md` § Release names first).
- An anime's episode for which no wish group produces a torrent is searched for on the indexers by show
  and episode (`indexer-search.md` § When no release name gives a torrent).
- An episode for which no torrent is found is shown with its reason on the Activity page, and is searched
  for again on the next run.
- When nothing a run started is still downloading, staged or waiting on an encode, maintenance runs
  and the run ends.

## While a season pack downloads

- A season pack downloads only the video files whose own name names an episode it was offered for. An
  episode with a video file in the library is not one of them, so its file in the pack is not downloaded.
- A season pack added back to the download client downloads the same video files.
- Once the download client knows a season pack's files, an episode it was offered for and holds no video
  file of is no longer answered for by it, and the next run searches for that episode.
- A season pack that holds no video file of any episode it was offered for is refused by the download
  client, and every one of those episodes is searched for again.

## When a download finishes

- A finished torrent that is not seeding, and holds a video for one of its episodes, is let go of by the
  download client, and each such video is moved into the intake folder. On one disk that is a rename and
  takes no time; across two disks the file system copies it. The episode's own name appears only once the
  whole file is there.
- A torrent let go of is still known to the client: it can be added back without asking its swarm, and
  its files can be named and deleted later.
- A torrent added by hand covers no episode, so what it holds is read when it finishes. Which episode a
  video is for comes from that video's own name. Which show it is comes from the same name, and where
  that names no show in a library, from the folder the torrent downloads into — which is the torrent's
  own name. A name carrying more than one title, as a fansub batch does, is matched on each of them, and
  the longest title a library holds wins. A video naming no show a library holds and sitting in no folder
  that does is left alone, and so is one naming no episode.
- When every video moved, what else that torrent downloaded, and the folders it came in, are deleted from
  the download folder. When none moved, the client holds the torrent again. When only some moved, the
  rest stays until the grab is done, and is deleted then.
- A download that was moved and could not be given its episode's name is put back where it was. Where
  that fails too it is kept in the intake folder under its temporary name, and never deleted.
- A torrent still seeding keeps its files: each video is copied into the intake folder, and the torrent
  and its files go once seeding is over and the library has the episode.
- An encode is asked for each staged video. The grab is done once the library has the episode, or holds
  a file named for the episode under another episode and the server has not said the encode is still
  running. The server says nothing about an encode it skips because every output already exists.
- For an episode to be replaced, once its new release has downloaded whole and its video is staged,
  the files of the old episode's encode go to the server's recycle area first, and then the encode is
  asked for. Those files are the episode's own folder in the library — the folder the library's file for
  the episode sits in, named for the episode, inside the show's folder — and in it only the files the
  encoder writes (the master playlist the library names, `.nomercy.json`, `manifest.json`,
  `chapters.vtt`, `fonts.json`, the sprite `thumbs_{w}x{h}.webp` and `.vtt`, and everything in `video_*`,
  `audio_*`, `subtitles` and `fonts`), written between the old delivery's grab and the new one's. The file
  the library names goes first. The rest of the library is never touched: not the show's folder, not
  another episode's, not a file in a recycle area, not a file from before the old delivery, not a file
  the encoder does not write — an `.nfo`, the owner's extras.
- A delivery is done with its encode's master playlist written down: when it was last written and how long
  it is. A replacement recycles only while the playlist the library names is still that one, to the tick
  and the byte; otherwise the encode is somebody else's — the owner's, made by hand while the replacement
  was waited for — and nothing is recycled, nothing encoded, and why is said. A delivery done before this
  was kept has nothing written down, and the time window alone speaks for it.
- The server recycles a file under its own name into a `.recycled` folder beside it and never over one
  already there. Where any of the old encode's files has such a copy already, nothing is recycled and the
  replacement waits, saying why, until the owner takes that copy out.
- Where the owner has granted the plugin no library to write in, the server does not let it write in
  the episode's library, or the server refuses, nothing is recycled and nothing is encoded: the new video
  waits in the intake folder, and the Downloads page, the log and the Activity and History pages say why,
  once. Every later look tries again.
- The old episode's files are recycled once. A later look that asks for the encode again recycles
  nothing.
- A grab for an episode to be replaced is done once the file the library names for the episode was written
  after the old encode went to the recycle area — never on the library having the episode (it had it all
  along) and never on the server saying the encode finished (media-server #599 said so of one still
  running). While the server says the encode is queued or running it is not done either way.
- The plugin looks at what the download client holds when the client says a download finished, at the
  start of every run, once a torrent the client was handed has its grab written down, and every minute
  while a finished download waits to be staged or a grab waits on an encode. A plugin with nothing waiting looks at nothing between runs.
- A download that finishes while the plugin is looking is looked at again before that look ends.
- A finished download whose staging fails stays where it is and is staged by a later look, with nothing
  else happening.
- A finished download whose staging failed is tried again once a minute, by the look that comes round
  while it waits, or at the start of a run. The download client saying it is finished again does not try
  it sooner.
- While a grab waits on an encode, each look reads what the server made of it and the library, and the
  look that finds the episode closes the grab.
- A move or a copy into the intake folder that fails is written to the server's log as a warning naming
  the file's path, the intake folder and the reason. A staging that fails for any other reason is a
  warning naming the download folder and the reason, and a look that fails is a warning naming the
  reason.
- A grab whose staged files are gone for every episode not yet in the library, while the server does
  not say an encode is running, fails, and its episodes are searched for again.
- An episode whose file is in the library under its own name is not missing, whichever episode the
  server registered that file against.

## When a site or a source fails

- A question to a site that does not answer, or that stays behind a challenge, is asked at most twice,
  each time after the challenge is solved again.
- A site that still gives no answer after the second attempt is left out of the rest of that run, and
  the Sources page shows why.
- A name source whose feed or search fails gives no release names in that run, and the run carries on.
- Stop ends the run. Downloads already offered to the download client carry on.

## After an update

- When the plugin is updated through the catalogue while the server runs, the server goes on serving the
  endpoints of the copy it started with (media-server #60). Until the server is restarted, every button
  answers that the plugin was updated while the server ran and that a restart puts it right. The plugin
  does not re-attach its endpoints itself: that reaches the server's own registrar outside the SDK, which
  FiLL/nomercy-torrent-plugin#1 rules out. The pages themselves are drawn by the updated copy from the
  first fetch.
- An update whose manifest asks for more than the owner approved before — the encoder, from 0.7.0, and
  library write, from 0.8.1 — loads disabled until the owner approves it once more on the plugin's page.
  The server asks; the plugin cannot. Library write is granted per library, by the owner.

## What the plugin deletes

- The plugin deletes only files and folders it created itself.
- In the library the plugin deletes nothing. The encoded files of an episode it delivered from a release
  no name source names go to the server's recycle area, where the owner can get them back, and only once
  the release that replaces it is staged (§ When a download finishes). Nothing else in the library is
  moved or recycled.
- In the intake folder that is a file the plugin staged, and only once nothing waits on it and no
  encode is reading it. A folder in the intake folder is never deleted, and neither is a file the
  plugin did not stage, whatever it is called.
- In the download folder that is a file a torrent of the plugin's names, and a folder those files were
  in once nothing else is left in it. A file a torrent does not name is never deleted, and a folder
  still holding one is kept.

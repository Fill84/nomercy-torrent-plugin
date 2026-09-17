# A run

## What starts a run

- A run starts when the owner presses Run, when the server finishes a library scan, and when the run
  interval comes round.
- A start that arrives while a run is going is added to that run.

## The order of a run

- A run first works out the episodes being searched for: the aired episodes without a video file of
  the shows and anime that are switched on with a quality, their own or their library's.
- The episodes are worked through in order of show, season and episode.
- A run reads the feeds of all name sources at the same time before it works on any episode.
- A feed release name is taken for an episode when its show and its season and episode number are
  those of an episode being searched for. Every other feed release name is left.
- The episodes are worked on one at a time, and an episode's winning torrent is offered to the
  download client before the next episode is worked on.
- For an episode, the release names are the feed release names taken for it. An episode with no feed
  release name is looked up in the search of every name source.
- For an episode, the indexers are asked for one wish group at a time, starting with the group
  carrying the most wishes.
- The first-choice indexers are asked one after another in their order, and then every other enabled
  indexer at the same time.
- A torrent found first is one found on an indexer earlier in that order, and among the other
  indexers one earlier in the source catalogue.
- A question already asked of an indexer during a run is not asked of it again in that run.
- An episode for which no wish group produces a torrent is searched for on the indexers by show and
  episode (`indexer-search.md` § When no release name gives a torrent).
- An episode for which that search produces no torrent either is shown with its reason on the Activity
  and History pages, and is searched for again on the next run.
- When nothing a run started is still downloading, staged or waiting on an encode, maintenance runs
  and the run ends.

## When a download finishes

- A finished torrent that is not seeding, and holds a video for one of its episodes, is let go of by the
  download client, and each such video is moved into the intake folder. On one disk that is a rename and
  takes no time; across two disks the file system copies it. The episode's own name appears only once the
  whole file is there.
- A torrent let go of is still known to the client: it can be added back without asking its swarm, and
  its files can be named and deleted later.
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

## What the plugin deletes

- The plugin deletes only files and folders it created itself.
- In the intake folder that is a file the plugin staged, and only once nothing waits on it and no
  encode is reading it. A folder in the intake folder is never deleted, and neither is a file the
  plugin did not stage, whatever it is called.
- In the download folder that is a file a torrent of the plugin's names, and a folder those files were
  in once nothing else is left in it. A file a torrent does not name is never deleted, and a folder
  still holding one is kept.

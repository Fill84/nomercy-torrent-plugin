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
- An episode for which no wish group produces a torrent is shown with its reason on the Activity and
  History pages, and is searched for again on the next run.
- When nothing a run started is still downloading, staged or waiting on an encode, maintenance runs
  and the run ends.

## When a site or a source fails

- A question to a site that does not answer, or that stays behind a challenge, is asked at most twice,
  each time after the challenge is solved again.
- A site that still gives no answer after the second attempt is left out of the rest of that run, and
  the Sources page shows why.
- A name source whose feed or search fails gives no release names in that run, and the run carries on.
- Stop ends the run. Downloads already offered to the download client carry on.

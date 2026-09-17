# Indexer search and the winning torrent

## Asking the indexers

- Each chosen release name is searched on an indexer letter for letter, as the name source wrote it.
- An indexer that has nothing for the exact name is searched with the same name without its
  punctuation.
- TorrentBay, LimeTorrents and Nyaa are the first-choice indexers.
- For a show, TorrentBay and LimeTorrents are asked first.
- For an anime, Nyaa is asked first, then TorrentBay and LimeTorrents.
- Every other enabled indexer is asked after the first-choice indexers, for the same release name.
- Every enabled indexer is asked. A result on a first-choice indexer does not end the search.
- An indexer result counts only when its title is the release name that was searched for.
- A title is the release name when the two are the same letters and digits in the same order, with
  case, punctuation and the site's own tag on the title set aside.
- An indexer result is not judged against the show's quality, codec, wishes, musts or forbidden.

## Merging by hash

- Results with the same info hash, from any number of indexers, are merged into one torrent.
- A merged torrent's magnet carries every tracker that any of its results gave.
- Results with the same info hash are always merged, whichever torrent wins.

## The winner

- The merged torrent found on the most indexers wins.
- Of merged torrents found on the same number of indexers, the one found on a first-choice indexer
  wins.
- For an anime, of merged torrents found on the same number of indexers, the one found on Nyaa wins
  over the one found only on TorrentBay or LimeTorrents.
- Of merged torrents that are still level, the one found first wins.
- A release or hash that the download client failed and that is still refused takes no part in
  choosing the winner.
- When the winner's magnet or torrent cannot be read from any indexer that listed it, the next merged
  torrent in the same order becomes the winner.

## When no release name gives a torrent

- When no wish group of release names gives a torrent, the indexers are asked for the show's title with
  the season and episode, as `South Park S15E12`.
- The indexers are asked in the same order: the first-choice indexers one after another, then every
  other enabled indexer at the same time.
- A result counts only when its title names that show, that season and that episode and nothing more,
  and it meets the show's settings: quality, codec, English only, musts, forbidden and the refused
  releases.
- A result that its indexer dates more than a week before the episode aired does not count: another
  programme of the same title answers the same search with older results. A result with no date counts
  as its title says.
- The results that count are merged by hash, grouped by the wishes they carry — the group carrying the
  most wishes first — and the winner of a group is chosen and handed over as for a release name.
- A result's own title is the release name it is downloaded under.

## Handing the winner over

- The winning merged torrent is offered to the download client.
- Only the winning torrent is offered. No other hash of the same release is started.

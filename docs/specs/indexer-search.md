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
- A season pack is asked of the indexers, merged and its winner chosen exactly as a release name for an
  episode, with the latest-aired episode it would cover as the episode a result is dated against
  (`release-names.md` § Season packs). When no merged torrent of it can be offered, the season's episodes
  are searched for one by one.

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
- A merged torrent whose every indexer result reports nought seeders is not offered to the download
  client, and the next merged torrent in the same order becomes the winner.
- A result with no seeder count is not nought: a merged torrent with such a result is offered.
- When every merged torrent left reports nought seeders, the episode is not grabbed, the Activity and
  History pages say "no seeders", no release or hash is refused for it, and the next run searches for the
  episode again.

## When no release name gives a torrent

- For a show, when no wish group of release names gives a torrent, nothing is downloaded and the next run
  searches for the episode again. The indexers are not asked for the show and episode
  (`release-names.md` § Release names first).
- For an anime, when no wish group of release names gives a torrent, the indexers are asked for the show's
  title with the season and episode, as `Solo Leveling S02E01`. Everything below is about that search.
- The indexers are asked in the same order: the first-choice indexers one after another, then every
  other enabled indexer at the same time.
- A result counts only when its title names that show, that season and that episode and nothing more,
  and it meets the show's settings: quality, codec, English only, musts, forbidden and the refused
  releases.
- A result that any indexer dates more than a week before the episode aired does not count, on any
  indexer that lists the same torrent: another programme of the same title answers the same search with
  older results.
- Where the name sources answered the episode with release names published long before it aired, another
  programme of the same title is known, and only a result with a date counts. Otherwise a result with no
  date counts as its title says.
- The results that count are merged by hash, grouped by the wishes they carry — the group carrying the
  most wishes first — and the winner of a group is chosen and handed over as for a release name.
- A result's own title is the release name it is downloaded under.

## Handing the winner over

- The winning merged torrent is offered to the download client.
- Only the winning torrent is offered. No other hash of the same release is started.
- On a run over every missing episode, a merged torrent an open grab is already downloading is not
  offered again, and the next merged torrent in the same order becomes the winner. A season pack that no
  longer answers for some of its episodes is not taken again for them, and they are searched for singly.

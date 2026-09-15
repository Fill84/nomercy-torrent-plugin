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

## Handing the winner over

- The winning merged torrent is offered to the download client.
- Only the winning torrent is offered. No other hash of the same release is started.

# Release names

## Release names first

- An indexer is searched first with the release names that a name source gave.
- When no release name gives a torrent — because no name source gave a release name that meets the
  show's settings, or because no indexer listed a torrent for any of them — the indexers are searched
  for the show and episode instead (`indexer-search.md` § When no release name gives a torrent).
- The plugin builds no other search term of its own from a show's title, season or episode.

## Reading the feeds

- On every run the plugin reads the latest feed of every name source: PreDB, srrDB, PreDB.net and
  SceneSource.
- The name sources are the same four for a show and for an anime. Nyaa is an indexer and not a name
  source.
- A run starts every hour unless the owner sets a different interval.
- The shortest interval the plugin accepts is 15 minutes. Any longer interval is accepted.
- A feed item is matched only against shows and anime that are switched on with a quality.
- A feed item is taken for an episode of such a show only when that episode has aired and has no
  video file in the library.

## Backfill

- On every run, every aired episode without a video file of a show that is switched on with a
  quality, and that no feed item was taken for, is looked up by show and episode in the name
  sources' search.
- A release name that search returns is judged and chosen exactly as a release name from a feed.

## Choosing the release name

- A release name names one episode of one show by its season and episode number. A release name
  without an episode number, such as a season pack, is not searched for.
- A release name that carries a year after the show's title names the show of that year. It is not
  searched for when that year is more than one year from the show's own.
- A release name that its name source dates more than a week before the episode aired is not a name for
  that episode: another programme of the same title answers the same search with older names.
- A release name is judged against the settings of its show: its resolution is the show's quality,
  its codec is the show's codec, it carries every must tag and it carries no forbidden tag.
- A release name that fails any of those is not searched for.
- Of the release names that pass, the names that carry the most wishes are searched first.
- When the names carrying the most wishes produce no torrent on any indexer, the names carrying the
  next highest number of wishes are searched, and so on down to names carrying no wish.
- Release names carrying the same number of wishes are searched together, and the winning torrent is
  chosen across all of them.
- No show setting is applied anywhere except to release names.

# Release names

## Release names first

- An indexer is searched first with the release names that a name source gave.
- A show downloads only a release that a name source names. For a show, an indexer is searched only with
  the release names that a name source gave.
- A show's episode for which no name source gave a release name that meets the show's settings is not
  searched for on any indexer and nothing is downloaded for it. The Activity and History pages say why,
  and the next run searches for it again.
- A show's episode whose release names give no torrent on any indexer is not downloaded either, and the
  next run searches for it again.
- An anime is the exception: fansubs are its real releases and no name source lists them. When no
  release name gives an anime's episode a torrent — because no name source gave a release name that meets
  the show's settings, or because no indexer listed a torrent for any of them — the indexers are searched
  for the show and episode instead (`indexer-search.md` § When no release name gives a torrent).
- The plugin builds no other search term of its own from a show's title, season or episode than the
  show and season for a season pack (§ Season packs), and for an anime the show and episode.

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

## Season packs

- A season has several gaps in a run when two or more of its aired episodes without a video file, of a
  show switched on with a quality, are being searched for in that run. An episode an open download
  already answers for is not being searched for.
- A season pack is a release name that carries the show's title and the season and no episode number, as
  `Show.S03.1080p.WEB.H264-GROUP`, for a show and for an anime alike.
- An anime batch that names no season — `01~12`, `Batch`, `Complete` — is not a season pack and is not
  searched for.
- A season with several gaps is looked for as a season pack before any of its episodes is searched for.
- A feed release name is taken for a season with several gaps when it is a season pack of that show and
  that season.
- A season with several gaps that no feed season pack was taken for is looked up in the search of every
  name source by the show's title and the season, as `Silo S03`, and only the season packs of that show
  and season are kept.
- A season pack is judged against the settings of its show exactly as a release name for an episode is,
  and is searched for on the indexers exactly as one is (`indexer-search.md`).
- A season pack that any name source dates more than a week before the latest-aired episode it would
  cover aired is not a name for that season.
- The winning torrent of a season pack is offered to the download client once, for every episode of the
  season being searched for, and those episodes are not searched for one by one in that run.
- When no season pack gives a torrent, each episode of the season is searched for one by one.
- A season with one episode being searched for is not looked for as a season pack.
- The indexers are asked for the show and episode (`indexer-search.md` § When no release name gives a
  torrent) for an anime's episode only, never for a season.

## Choosing the release name

- A release name for an episode names one episode of one show by its season and episode number. A
  release name without an episode number is not a release name for an episode.
- A release name that carries a year after the show's title names the show of that year. It is not
  searched for when that year is more than one year from the show's own.
- A release name that any name source dates more than a week before the episode aired is not a name for
  that episode, whichever source gave it: another programme of the same title answers the same search with
  older names.
- A torrent an indexer dates more than a week before the episode aired is not taken for it, whether it was
  found for a release name or, for an anime, for the show and episode.
- A release name is judged against the settings of its show: its resolution is the show's quality,
  its codec is the show's codec, it carries every must tag and it carries no forbidden tag.
- A release name that fails any of those is not searched for.
- Of the release names that pass, the names that carry the most wishes are searched first.
- When the names carrying the most wishes produce no torrent on any indexer, the names carrying the
  next highest number of wishes are searched, and so on down to names carrying no wish.
- Release names carrying the same number of wishes are searched together, and the winning torrent is
  chosen across all of them.
- No show setting is applied anywhere except to release names.

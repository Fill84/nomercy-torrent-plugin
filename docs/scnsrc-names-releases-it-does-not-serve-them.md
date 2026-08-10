# SCNSRC names releases; it does not serve them

Found on a real server, 11 August 2026, by comparing this plugin against the owner's own
`torrent-feed` (`github.com/Fill84/BeastStack/tree/main/torrent-feed`).

## What is wrong

This plugin treats SCNSRC as an indexer that hands over torrents. `RssIndexer.ToRelease`
reads `item.EnclosureUrl` as the download and `item.Link` only when it is a magnet.
SCNSRC gives neither: its items link to a web page.

So the feed cadence matches correctly and then grabs nothing. It is the
`matched 40, grabbed 0` case that `FeedCycle` was built to report - which was written as
though it were the exception, and for this indexer it is the normal state.

## What torrent-feed does instead

Three sources, three different jobs. From its own `resolver.py`:

> SCNSRC names the exact scene release for an episode, so a candidate whose title IS that
> release is the strongest evidence it is the genuine thing -- it wins outright. Failing
> that TorrentBay is preferred, then seeder count.

SCNSRC is the announcement: it says a release exists and what it is called. TorrentBay and
LimeTorrents are searched for that exact name, and the magnet comes from one of them.
Either of the two failing is survivable; whatever the other returned is still used.

## The change

A resolve step between choosing and grabbing. When the chosen release has no `MagnetUri`
and no `DownloadUrl`, its title becomes the query: search the other indexers for that exact
name and take the magnet from the best match, ranked the way the resolver ranks - exact
title first, then source preference, then seeders.

That makes SCNSRC what it actually is, and makes the owner's description true of this
plugin too: give it a title, and the rest follows.

Notes for whoever builds it:

- The rank is not the quality profile. The profile already chose *which release*; this is
  only about *where to get it*, so an exact title match must win over a higher-scoring
  different release.
- A resolve that finds nothing is not a failed episode. Leave the grab unmade and the
  episode wanted, and do not spend a search attempt - the same rule the feed cadence uses.
- Verify the payload before handing it on. `torrent-feed` checks LimeTorrents winners
  specifically, because that is where mislabelled contents show up.

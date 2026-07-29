# Captured indexer fixtures

Real responses captured from live indexers, carried over from the `torrent-feed`
prototype. Stage 0b's indexer tests parse these rather than hand-written samples.

That distinction is not cosmetic. Stage 0a was built against invented release
titles, and the majority of the defects found in review came from cases the
invented data politely avoided: a show whose name is a language word, a name
carrying a diacritic, an indexer that suffixes every title with `[eztv.re]`.
Real captures do not flatter the parser.

| File | Source | Notes |
| --- | --- | --- |
| `torrentbay-browse.html` | TorrentBay browse page | Full listing, via FlareSolverr |
| `torrentbay-search.html` | TorrentBay search page | Search-term highlighting present, which is what glues tokens together |
| `torrentbay-magnet-response.json` | TorrentBay magnet AJAX | Wrapped in `<pre>`, HTML-escaped |
| `limetorrents-search.html` | LimeTorrents search | Infohash in the row, so magnets cost no extra request |
| `scnsrc-feed.xml` | SCNSRC scene feed | Category-tagged; carries date-stamped daily shows |
| `silo-cakes.torrent` | itorrents mirror | Single-file video payload |
| `lucky-ethel.torrent` | itorrents mirror | Multi-file payload |

Do not regenerate these casually. Their value is that they are a fixed record of
what these sites actually returned on a given day.

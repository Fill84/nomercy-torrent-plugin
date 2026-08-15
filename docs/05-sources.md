# Sources

Seventeen sources ship in `src/.../sources.json`, copied beside the assembly. On top of them the
owner may add their own indexers and their own private trackers.

All seventeen were measured working end to end on 13 August 2026 — each answered a real release name
and produced a route to a torrent. The traps below are what that took.

## Roles

| Role | Answers | Asked with |
| --- | --- | --- |
| **Feed** | what was released recently | nothing — read whole. A feed answers any question with the newest N posts |
| **Name database** | what a release is called | show and slot; answers names, never torrents |
| **Indexer** | who is serving a named release | the **full release name**; answers rows with hashes |

A feed with a search address is both a feed and a name database. `SourceRole` is decided from `kind`
and the presence of `searchUrl`, and nothing else guesses.

## The shipped catalogue

| Source | Kind | Reader | Query | Gated | Prio | Interval | Role |
| --- | --- | --- | --- | --- | ---: | ---: | --- |
| PreDB | `rss` | — | words | no | 20 | 60s | feed + names |
| srrDB | `rss` | — | words | no | 20 | 60s | feed |
| srrDB search | `srrdb` | — | slug | no | 20 | 15s | names |
| SceneSource | `rss` | — | words | **yes** | 20 | 60s | feed |
| EZTV latest | `eztv-api` | — | words | no | 30 | 60s | feed |
| The Pirate Bay | `apibay` | — | words | no | 45 | 5s | indexer |
| 1337x | `site` | `1337x` | words | **yes** | 40 | 15s | indexer |
| LimeTorrents | `site` | generic | words | no | 35 | 15s | indexer |
| TorrentBay | `site` | `torrentbay` | words | **yes** | 30 | 15s | indexer |
| EZTV | `site` | `eztv` | words | **yes** | 30 | 15s | indexer |
| KickassTorrents | `site` | `kickass` | words | **yes** | 25 | 15s | indexer |
| TorrentGalaxy | `site` | `torrentgalaxy` | **spaced** | no | 30 | 15s | indexer |
| Torrentz2 | `site` | `torrentz2` | words | no | 25 | 15s | indexer |
| TorrentDownloads | `site` | `torrentdownloads` | words | no | 25 | 15s | indexer |
| TorrentFunk | `site` | `torrentfunk` | **slug** | no | 25 | 15s | indexer |
| Nyaa | `torrent-rss` | — | words | no | 30 | 15s | indexer (anime) |
| YTS | `yts` | — | words | no | 20 | 15s | films — **off** |

```
PreDB            https://predb.me/?rss=1
                 https://predb.me/?search={query}&rss=1
srrDB            https://www.srrdb.com/feed/srrs
srrDB search     https://api.srrdb.com/v1/search/{query}
SceneSource      https://www.scnsrc.me/feed/
EZTV latest      https://eztv.re/api/get-torrents?limit=100&search={query}
The Pirate Bay   https://apibay.org/q.php?q={query}&cat=
1337x            https://www.1337x.to/sort-category-search/{query}/TV/time/desc/1/
LimeTorrents     https://www.limetorrents.lol/search/all/{query}/
TorrentBay       https://extranet.torrentbay.st/browse/?q={query}&sort=seeders&order=desc
EZTV             https://eztvx.to/search/{query}
KickassTorrents  https://katcr.to/usearch/{query}/
TorrentGalaxy    https://torrentgalaxy.one/get-posts/keywords:{query}/
Torrentz2        https://torrentz2.nz/search?q={query}
TorrentDownloads https://www.torrentdownloads.pro/search/?search={query}
TorrentFunk      https://www.torrentfunk.com/all/torrents/{query}.html
Nyaa             https://nyaa.si/?page=rss&q={query}
YTS              https://yts.gg/api/v2/list_movies.json?query_term={query}
```

Every shipped host is declared in `plugin.json` under `capabilities.network.hosts`. A host the
server has not permitted refuses instantly and reads exactly like a site with nothing to offer. A
test keeps the two lists in agreement in both directions.

## The owner's own sources

A manifest cannot know a host the owner types in, so those are requested at runtime through
`IPluginGrants.RequestAsync(NetworkHost, host, reason, ct)` and the plugin says out loud which hosts
it is waiting on.

**Own indexers.** Name, address with `{query}`, kind (`torznab` or `site`), priority, minimum
interval, optional API key, enabled. Validated on save: the address must be absolute and contain the
placeholder. The API key is stored through `IPluginSecretStore` and never rendered.

**Own private trackers.** Host and announce URL with passkey. The passkey is a secret: stored
through `IPluginSecretStore`, never rendered, never logged, never in an error message, never in the
activity journal. A torrent whose metadata says `private` announces only to its own tracker, with
DHT, peer exchange and local discovery disabled for that torrent.

An owner-configured source with the same name as a shipped one replaces it. A shipped source named
in `DisabledDefaultSources` is dropped.

## Query styles

| Style | Sends | For |
| --- | --- | --- |
| `words` | punctuation to spaces, joined with `+` | the default |
| `spaced` | punctuation to spaces, joined with `%20` | a site searching from its **path**, where a plus is a plus |
| `slug` | lowercase, everything else to a single dash | a site whose search *is* the path segment |
| `verbatim` | as given | an endpoint matching a string rather than tokenising |

`spaced` versus `words` is declared per site and cannot be derived: TorrentGalaxy and 1337x both
search from their path and want opposite things.

## Per-site readers

**1337x** (gated) — category-scoped address only; a plain search with dots returns their error page,
which says "No results were returned" and is not an error. No magnet on the listing: the row carries
its own page address.

**EZTV** (gated; also `eztv-api` as a feed) — appends its own tag to every title; strip it or nothing
matches. Measured `[eztv.re]` on 13 August 2026 and **`[eztv]`** on 14 August 2026, so the reader
strips both: a site that has changed this once will change it again. The listing carries **no magnet**
— the links sit behind a POST form — so the row's own page is the route, as with 1337x. The API form
is JSON and must not be read as HTML.

**KickassTorrents** (gated) — four anchors per row and three empty; the name is in `cellMainLink`,
cut into highlighted fragments that must be joined — read the anchor whole and strip its tags rather
than joining its text nodes. The listing carries **no magnet**: the row's own page is the route.

0.3.4 measured that **a search for a full release name redirects to that release's own page**. It
does not, as of 14 August 2026: the same search answers a listing with one row in it, and
`tests/fixtures/kickasstorrents-full-name.html` is that page. The reader keeps the fallback — no
rows, so take a magnet anywhere on the page — because a site that did this once may do it again, but
**no capture demonstrates it and no test covers it**.

**TorrentBay** (gated) — publishes no magnet. The magnet comes from a **signed POST** to its own
endpoint, built from two values off the row and two off the search page, sent from inside the
browser session. `[GeneratedRegex]` was measured returning zero matches here where the identical
inline `Regex` returned fifty — use `static readonly Regex`.

**TorrentGalaxy** — `torrentgalaxy.one`; query style `spaced`. No magnet and no hash on the listing:
the dozen forty-character hex strings are element ids. Title from the anchor's `title` attribute —
the text is split across spans and joining the nodes glues words together. Seeders sit behind
`title="Seeders/Leechers"`, two tags away from the bracket.

**Torrentz2** — twenty definition lists, each with a name and a link to its own page. Titles read
`www.UIndex.org - Silo.S03E06...`; cut the prefix, anchored on ` - ` with spaces, because a scene
name is full of dashes and the one before the group has none.

**TorrentDownloads** — the first link is an advert for another site carrying the same terms. Match
rows on the numeric id every real release has in its address. Seeders and leechers are two bare
spans in that order followed by the size.

**TorrentFunk** — query style `slug`; a query with spaces gets a 301 to nothing. Attributes are
**bare**: `class=tv3`, not `class="tv3"`. The name is split by a span colouring the group, so read
the anchor whole and strip its tags. The detail page carries **no magnet** — its download button
leads to an advertising redirect on a third host — but it prints the bare info hash. Read a bare
forty-hex string as a hash **only when the page has exactly one**.

**LimeTorrents** — a hashed `.torrent` link on the listing; the generic reader handles it.

**The Pirate Bay** — JSON at apibay; the website is a JavaScript shell with no results in it.
Rate-limits hard under a burst.

**Nyaa** — an indexer in XML; every item links a real torrent. For anime it is often the only source
that has the release, and it is asked with both the seasonal and the absolute form.

**srrDB / srrDB search** — name databases. `api.srrdb.com/v1/search/{query}` answers JSON with
`resultsCount` and `results[].release`. A show with no scene releases honestly answers zero; that is
not a broken reader.

**PreDB** — name database with an RSS search, paced at sixty seconds. `predb.me` answers a plain
`curl` with an empty feed but answers the plugin; do not conclude it is dead from a shell test.

**SceneSource** (gated) — read through the browser. **It is a feed and has no search.** 0.3.4 put it
in the search set and made forty identical requests per cycle.

## Fetching

1. A **gated** host goes straight to the browser.
2. Everything else tries plain HTTP first, through the host gate.
3. A challenge falls through to the browser, once. A second challenge after a fresh solve gives up.
4. A JSON or XML endpoint fetched through the browser is re-fetched **inside the page** so the body
   is the body, not Chrome's viewer for it.
5. Gating is a property of an **address**, not a site: PreDB answers its feed over plain HTTP and
   puts its search behind a challenge.

Every error names the address it failed on, with anything matching
`api_?key|apikey|passkey|token|secret|rss_?key` blanked out.

## Merging

The same torrent on five sites is one torrent with five sets of trackers. Merge by info hash, union
the trackers, take the highest seeder count, keep the announced title. More trackers is a faster
download, which is why every indexer is asked.

Ranking between two different acceptable torrents: seeders first, then indexer priority
**descending**. 0.3.4 had this inverted and picked the worst-rated site.

## The health tool

`tools/SourceHealth` walks every enabled source through the real chain — same catalogue, same fetch,
same reader — with a real release name in and a magnet out. It writes `health/report.md` and the page
each source returned.

A source is flagged when it does not answer, offers no route to a torrent, returns far fewer rows
than last time, or — the case it exists for — when **the page is covered in torrents and the reader
saw none of them**. It distinguishes that from a site that honestly has nothing by counting
release-shaped **names** in the body: six of the seventeen answer JSON or XML with no anchor and no
magnet anywhere in them, so a count of links would report every one of those as having nothing on
the day its reader broke. A name is release-shaped when it carries a resolution, a codec or a
source — never the episode number, which is in the term that was searched for and so appears on
every page that echoes the question back.

A route to a torrent is a magnet, an info hash **or the row's own page**. No shipped indexer
publishes a magnet on its listing, so insisting on one would flag all of them.

It clears the captured body between sources, and a source that rate-limits us is asked once more
after a wait rather than recorded as broken.

**Later, not now:** repairing a reader whose site changed is manual — the tool reports it, a fresh
capture is taken, the reader is fixed. Automating that repair is worth doing and is deliberately out
of scope for 0.4.0.

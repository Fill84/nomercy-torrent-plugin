# Sources

Sixteen entries ship in `src/.../sources.json`: five name sources and eleven indexers. They are
copied beside the assembly, and on top of them the owner may add their own indexers and their own
private trackers.

**Two ship switched off**: YTS, which is films and out of scope, and EZTV latest, whose API takes a
search parameter and ignores it. Fourteen are asked.

**The two are not the same thing and this document does not use one word for both.** A name source
answers what a release is called; an indexer answers who is serving it. `SourceRole` decides which
from the kind and the presence of a search address, and known failure **A2** is what happened when
they were confused — a feed was put in the search set and asked a question per episode, forty
identical requests a cycle. This page used to open by calling all fifteen "sources", and by naming
two different totals three lines apart.

**The name sources are PreDB, srrDB, PreDB.net and SceneSource, for a show and for an anime alike.
Five entries: srrDB ships as its feed and, separately, its search API.** The owner's rule of
10 September 2026, with PreDB.net added and Nyaa named an indexer on 15 September 2026
(`docs/specs/release-names.md`). Everything else in the catalogue is an
indexer, EZTV included: its endpoint answers rows carrying a magnet, a hash, a seed count and a
size, which is what an indexer answers and not what a scene database does. It was counted a feed
until then, and 3,149 of its file names — a quarter of the owner's whole name pool — went in as
release names, spelled `Somebody.Knows.Something.S01E04.XviD-AFG[EZTVx.to].avi` with the site's tag
inside and the extension still on the end.

Fourteen are asked. YTS ships switched off because films are out of scope, and EZTV latest because
its API ignores the search term: measured 11 September 2026, it answered every question for every
episode with the same hundred newest torrents of every show, so it counted as having answered and
was asked the same thing over and over. The owner's decision; the EZTV site, which searches, stays. All of them were measured
working end to end on 13 August 2026 — each answered a real release name and produced a route to a
torrent. The traps below are what that took.

**Every source is on unless the owner switches it off.** The Sources page draws a switch for each
under Show advanced, and `DisabledDefaultSources` records the ones that are off — see
`docs/08-ui.md` § Sources. A shipped source is never editable: its address and reader are this
plugin's, tested against a capture.

## Roles

| Role | Answers | Asked with |
| --- | --- | --- |
| **Feed** | what was released recently | nothing — read whole, every one at once at the start of every run. A feed answers any question with the newest N posts |
| **Name database** | what a release is called | `Show SxxEyy` and nothing else, for an episode no feed named; answers names, never torrents |
| **Indexer** | who is serving a named release | the **full release name** letter for letter, then without its punctuation when that found nothing; answers rows, some with hashes |

A feed with a search address is both a feed and a name database. `SourceRole` is decided from `kind`
and the presence of `searchUrl`, and nothing else guesses.

## How a run uses the name sources

`Core/Pipeline/NameSources.cs`, `docs/specs/release-names.md` and `run.md`. Nothing is kept between
runs — the name pool is gone (migration `014`).

1. Every feed is read at once, before any episode is worked on. A feed name is taken for an episode
   being searched for when it names that show, that season and that episode (`EpisodeNaming`): the
   show's title with its words run together, or leading the name with only a year or a country after
   it. A season pack, a run of episodes, an absolute-numbered anime post and a film name no episode.
2. An episode no feed named is looked up in every name source's search at once, asked `Show SxxEyy` —
   no quality, no year, no absolute number. What the search answers for another episode is left.
3. A feed or a search that fails gives nothing that run; the others are read all the same.

**Measured on 15 September 2026** through the plugin's own fetch, and saved as `tests/fixtures/names-*`:
the four feeds answered 40 (PreDB), 100 (srrDB), 20 (PreDB.net) and 40 (SceneSource) items. PreDB, srrDB
and PreDB.net carry largely the same scene posts, and srrDB's feed spells its dashes as `&#45;`, which the reader
turns back into dashes. SceneSource prints names with spaces and often with the episode's title
inside — `Silo S02E01 The Engineer 1080p ATVP WEB-DL DDP5 1 Atmos H264-FLUX` — and carries season packs
such as `Forever Home S01 1080p MY5 WEB-DL AAC2.0 H.264-TBN`. Asked `Silo S02E01`, PreDB answered 21
names, srrDB 21, PreDB.net 20 and SceneSource 1, every one of them for that episode.

## The shipped catalogue

| Source | Kind | Reader | Query | Gated | Prio | Interval | Role |
| --- | --- | --- | --- | --- | ---: | ---: | --- |
| PreDB | `rss` | — | words | no | 20 | 60s | feed + names |
| srrDB | `rss` | — | words | no | 20 | 60s | feed |
| srrDB search | `srrdb` | — | slug | no | 20 | 15s | names |
| PreDB.net | `rss` | — | words | no | 20 | 15s | feed + names |
| SceneSource | `rss` | — | words | **yes** | 20 | 60s | feed + names |
| EZTV latest | `eztv-api` | — | words | no | 30 | 60s | indexer — **switched off** |
| The Pirate Bay | `apibay` | — | words | no | 45 | 5s | indexer |
| 1337x | `site` | `1337x` | words | **yes** | 40 | 15s | indexer |
| LimeTorrents | `site` | generic | words | no | 35 | 15s | indexer, first choice 3 |
| TorrentBay | `site` | `torrentbay` | words | **yes** | 60 | 15s | indexer, first choice 2 |
| EZTV | `site` | `eztv` | words | **yes** | 30 | 15s | indexer |
| TorrentGalaxy | `site` | `torrentgalaxy` | **spaced** | no | 30 | 15s | indexer |
| Torrentz2 | `site` | `torrentz2` | words | no | 25 | 15s | indexer |
| TorrentDownloads | `site` | `torrentdownloads` | words | no | 25 | 15s | indexer |
| Nyaa | `torrent-rss` | — | words | no | **70** | 15s | indexer, **anime libraries only**, first choice 1 |
| YTS | `yts` | — | words | no | 20 | 15s | films — **off** |

*Prio* is the `priority` each entry still carries in `sources.json`. Since 15 September 2026 it
decides no winner: seeders and a site's rating decide nothing, and the first-choice order is
`firstChoice` (§ How a run asks the indexers, `docs/specs/indexer-search.md`).

```
PreDB            https://predb.me/?rss=1
                 https://predb.me/?search={query}&rss=1
srrDB            https://www.srrdb.com/feed/srrs
srrDB search     https://api.srrdb.com/v1/search/{query}
SceneSource      https://www.scnsrc.me/feed/
                 https://www.scnsrc.me/feed/?s={query}
EZTV latest      https://eztv.re/api/get-torrents?limit=100&search={query}
The Pirate Bay   https://apibay.org/q.php?q={query}&cat=
1337x            https://www.1337x.to/sort-category-search/{query}/TV/time/desc/1/
LimeTorrents     https://www.limetorrents.lol/search/all/{query}/
TorrentBay       https://extranet.torrentbay.st/browse/?q={query}&sort=seeders&order=desc
EZTV             https://eztvx.to/search/{query}
TorrentGalaxy    https://torrentgalaxy.one/get-posts/keywords:{query}/
Torrentz2        https://torrentz2.nz/search?q={query}
TorrentDownloads https://www.torrentdownloads.pro/search/?search={query}
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

**KickassTorrents was removed on 22 August 2026**, on the owner's decision, and its host is out of
the manifest with it. Asked a full release name that day it answered with no listing at all, and the
one magnet anywhere on the page belonged to a wallpaper pack — so the only thing it could contribute
was a stranger's torrent wearing the page's own title. The reader's fallback for that shape, which no
capture had ever demonstrated, is what would have handed it over. Nothing here reads that site any
more.

**TorrentBay** (gated) — publishes **neither a magnet nor a hash**, on the listing or on a row's own
page. Both carry a button and an id, and nothing else. The magnet comes from a **signed POST** to
`/ajax/getSearchMagnet.php`, sent over plain HTTP in the session the listing was read in — never from
a browser tab, see `docs/07-solver.md` § The signed POST:

```
torrent_id  the row's data-id, off .search-magnet-btn
hash        empty — the button carries none and the site's script posts it anyway
name        empty — the same
timestamp   unix seconds, now
hmac        SHA-256 over "{torrent_id}|{timestamp}|{pageToken}", lower-case hex
sessid      the content of <meta name="csrf-token">
```

`pageToken` is `window.searchPageToken`, declared inline on the search page; the detail page declares
`window.pageToken` instead and the same session. Both belong to the page they were read from, so they
travel with the row and a token from another page is refused. The answer is
`{"success":true,"url":"magnet:?…"}`, and a refusal has the same shape — reading one as an address
hands the client something that is not a torrent.

This is the reason the source could not simply be dropped. It sorts by seeders and publishes honest
counts, so its rows outranked every other site's: while the request was unwritten its copy was chosen,
followed, found to name no torrent, and the episode was reported as though nobody were serving it.
Since 15 September 2026 seeders rank nothing, and TorrentBay is a first-choice indexer
(`docs/specs/indexer-search.md`).
Fifty rows to a page and it answers more, so `pageParameter` is `page` and three pages are read.

`[GeneratedRegex]` was measured returning zero matches here where the identical inline expression
returned fifty

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

**TorrentFunk was removed on 22 August 2026**, on the owner's decision, and its host is out of the
manifest with it. It answered nothing for the episodes of the owner's own library while every other
site answered plenty. Nothing here reads that site any more.

**LimeTorrents** — a hashed `.torrent` link on the listing; the generic reader handles it.

**The Pirate Bay** — JSON at apibay; the website is a JavaScript shell with no results in it.
Rate-limits hard under a burst.

**Nyaa** — an indexer in XML; every item links a real torrent. For anime it is often the only source
that has the release. Like every indexer it is asked only for release names a name source gave; the
absolute form it was also asked with until 15 September 2026 is not searched for
(`docs/specs/release-names.md`).

It is the one source with a **library scope**: `"libraries": [ "anime" ]` in `sources.json`. A source
that names no library is asked about all of them, so the field switches nothing off by omission. A
television search does not ask Nyaa at all — a paced request per episode spent on a site carrying
almost no television is a request taken from the sources that would have answered.

**It is the first first-choice indexer for an anime**, which is what "ranked first for an anime show"
means: for anime it is often the only site with the release, so it is asked first, and of torrents
found on the same number of indexers the one Nyaa found wins over one found only on TorrentBay or
LimeTorrents (`docs/specs/indexer-search.md`). Until 15 September 2026 its priority above every
general indexer's said this; priority decides no winner now. For television it never comes up,
because it is not asked.

**srrDB / srrDB search** — name databases. `api.srrdb.com/v1/search/{query}` answers JSON with
`resultsCount` and `results[].release`. A show with no scene releases honestly answers zero; that is
not a broken reader.

**PreDB** — name database with an RSS search, paced at sixty seconds. `predb.me` answers a plain
`curl` with an empty feed but answers the plugin; do not conclude it is dead from a shell test.

**SceneSource** (gated) — read through the browser, both addresses, because both sit behind the same
challenge: a plain request to either is a 403, which reads exactly like the site refusing us.

**It does have a search, and this document said it did not.** `/feed/?s={query}` answers
`You searched for X` with an item per release, in the same RSS the plain feed uses — so the same
reader does both and nothing new had to be written. Captured 22 August 2026 as
`tests/fixtures/scenesource-search-silo.xml`.

**Where the wrong sentence came from.** 0.3.4 put SceneSource's *feed* address in the search set and
asked it forty times a cycle, once per episode, getting the same newest-posts answer every time. The
lesson taken from that was "it has no search". The lesson was "search the search address, once per
show" — the fault was the address, not the site. Since 15 September 2026 the search address is asked
`Show SxxEyy` for an episode no feed named (§ How a run uses the name sources).

**What it cost.** With SceneSource read only for what is new, a show that aired last week was left to
srrDB's archive, which answers with years of foreign and 2160p releases. The profile then refuses
them one after another, each with a good reason, and the page reads as a plugin working hard and
finding nothing. The release the owner wanted was one request away: `/feed/?s=Silo` returns the
whole season, 1080p, in English.

## Fetching

1. A **gated** host goes straight to the browser.
2. Everything else tries plain HTTP first, through the host gate.
3. A challenge is solved and the question asked again — twice at most, each after a fresh solve. A
   host that does not answer is asked a second time. Still nothing, and the site is left out of the rest
   of the run: the indexer round asks it nothing more, a name source gives no names in it, and the
   Sources page carries the reason (`docs/specs/run.md`).
4. A JSON or XML endpoint fetched through the browser is re-fetched **inside the page** so the body
   is the body, not Chrome's viewer for it.
5. Gating is a property of an **address**, not a site: PreDB answers its feed over plain HTTP and
   puts its search behind a challenge.

Every error names the address it failed on, with anything matching
`api_?key|apikey|passkey|token|secret|rss_?key` blanked out.

## How a run asks the indexers

`Core/Pipeline/IndexerRound.cs`, `docs/specs/indexer-search.md`. One wish group of release names at a
time, most wishes first.

1. **First-choice indexers one after another, then the rest together.** `firstChoice` in
   `sources.json`: Nyaa 1, TorrentBay 2, LimeTorrents 3. Nyaa serves only anime, so a show asks
   TorrentBay then LimeTorrents, and an anime Nyaa, TorrentBay, LimeTorrents. Every enabled indexer is
   asked; a result on a first-choice indexer does not end the round.
2. **Each name exactly, then without punctuation** where the exact name found nothing. A question
   already asked of an indexer this run is not asked again.
3. **A row counts only when its title is the name** — `TitleMatcher.Release`: letters and digits in
   order, with case, punctuation and the site's tag (`[TGx]`, a trailing `EZTV`, `[EZTVx.to].mkv`) set
   aside. A row is not judged against the show's settings.
4. **A counting row with no hash has its own page read for one** (or TorrentBay's signed request).
   Most indexers print no hash on a listing, and a row without one cannot be merged or counted. Only
   rows whose title is the name are read, one or two an indexer — C3 still holds.
5. **Merged by hash**, every tracker of every row on the one torrent. A row whose torrent cannot be
   read takes no part.
6. **The winner** is on the most indexers; level, the one found first — which is a first-choice
   indexer's find, and Nyaa's for an anime. A release or hash still refused takes no part. Only the
   winner is offered, and the next in order only when the winner cannot be offered. Seeders and a
   site's rating decide nothing.

**Measured on 15 September 2026**, `tests/fixtures/round-*`: `Silo.S02E01.1080p.WEB.H264-SuccessfulCrab`
was posted twice. The TGx upload (`87D8…`) is on LimeTorrents and The Pirate Bay with its hash, and on
1337x, TorrentGalaxy, Torrentz2 and TorrentDownloads behind the row's page — six indexers. The EZTV
upload (`41B0…`) is on The Pirate Bay, TorrentGalaxy and Torrentz2 — three; EZTV's own page for it
answered 451. Torrentz2 lists two more uploads of the name on it alone. TorrentGalaxy answers the exact
name with nothing and the name as words with the release. Nyaa had nothing for the scene anime name
`Solo.Leveling.S02E01.1080p.WEB.H264-SKYANiME`; TorrentBay had it. Torrentz2's page spells its magnet
with HTML entities and is read all the same. **The capture tool's check of TorrentBay's signed request
failed with `Failed to fetch`**, sent after the browser had closed the tab that loaded the page — step 0
of `S13-08`, because TorrentBay is a first-choice indexer.

## The health tool

`tools/SourceHealth` walks every enabled source through the real chain — same catalogue, same fetch,
same reader — with a real release name in and a magnet out. It writes `health/report.md` and the page
each source returned.

A source is flagged when it does not answer, offers no route to a torrent, returns far fewer rows
than last time, or — the case it exists for — when **the page is covered in torrents and the reader
saw none of them**. It distinguishes that from a site that honestly has nothing by counting
release-shaped **names** in the body: six of the fifteen entries answer JSON or XML with no anchor and no
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

# The five things still missing

Asked for on 11 August 2026, after the plugin was verified loading and rendering on a real
server. Audited against the code the same day; two of the five turned out to be partly
built already, and the order below is the dependency order, not the order they were asked
in.

## What already exists

**Several RSS feeds at once, already works.** `TorrentDownloaderSettings.Indexers` is a
list, and every entry carries its own name, URL, priority, categories and rate limit. The
settings page adds and removes them. What is missing is only *where* you can do it: it is
the settings page or nowhere.

**A magnet by hand, already works.** The downloads page takes a magnet, matches it by name
against the wanted list, and refuses one that matches nothing. That is one link for one
episode, not a source - see item 2 below for the difference.

## 1. A Cloudflare-passing fetcher

Everything else here depends on it, which is why it is first. A site the owner names may
sit behind a Cloudflare interstitial, and `HttpClient` gets the challenge page rather than
the listing. `torrent-feed` runs FlareSolverr beside itself for exactly this.

A plugin cannot ask its owner to run a second container, so this has to be in-process:
issue the challenge fetch, keep the clearance cookie and user agent per host, reuse them
until they are refused, and fetch again. Every scraper below goes through it.

Two rules worth writing down before anyone starts:

- A host that is not behind Cloudflare must not pay for this. Detect the challenge from the
  response, do not pre-empt it.
- Clearance is per host and expires. Keeping one and retrying once on refusal is the whole
  lifecycle; anything cleverer will be wrong in a way nobody can debug from a log.

## 2. A site the owner names, read as a source

The plugin knows `torznab` and `rss` and nothing else. A torrent site is neither: it is an
HTML listing that has to be parsed, and in TorrentBay's case the magnet is assembled from
tokens carried on the search page itself, so the row and its magnet cannot be fetched
independently.

This is the missing half of the resolver described in
[scnsrc-names-releases-it-does-not-serve-them.md](scnsrc-names-releases-it-does-not-serve-them.md):
SCNSRC says what a release is called, and a site like this is where the magnet actually
comes from. Neither is useful without the other, so build them together and test them
together.

New indexer kind, same `IIndexer` contract, same pacing. The parsing is per site and will
break when a site changes its markup - so it fails loudly with the site named, and one
broken site never stops the others.

## 3. Adding a feed from wherever you are

Once a source is more than a URL in a settings form, adding one has to be possible at the
moment the owner realises they want it - on the downloads page, beside a show, next to an
episode nothing was found for. Same handler, several entry points.

## 4. The UI overhaul

Deliberately last. The page can only lay out what exists, and after the three items above
there is more to show: which source a release came from, why an episode has nothing, what a
site is doing. Building the overhaul first means building it twice.

What is wanted: everything grouped per show rather than per episode row, every page linked
to the others, and one overview that answers "what is this plugin doing" without scrolling.

## 5. How

Grondig. Each of the four above is a slice with its own tests, verified on the server
before the next one starts. Nothing here is urgent enough to justify four half-built
subsystems, and the session that produced this document ended precisely because starting
them would have produced exactly that.

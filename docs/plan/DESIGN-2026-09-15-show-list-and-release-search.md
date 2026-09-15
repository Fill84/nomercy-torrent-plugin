# Design · The show list and the release search

Approved by the owner in five parts on 15 September 2026. **The requirements are `docs/specs/`** —
`show-list.md`, `pages.md`, `release-names.md`, `indexer-search.md` and `run.md` — and win over
everything below where the two differ. This page says how they are built: what stays, what is new,
what goes, and in what order. The slices are `S13-01` to `S13-12` in `SPRINTS.md`.

## What stays

Everything from the moment a winning torrent is offered to the download client: the BitTorrent
client, staging, the encode, the encoder's events, "the library decides", one cycle driven by events,
maintenance once nothing is in hand. Also the per-site readers, the challenge-aware fetch and the
browser, magnet and torrent reading from a detail page, `TrackerBook`, and the Downloads, History and
Sources pages.

## What is new

1. **Settings per show and per library**, in the plugin's database (migration 013):
   - `show_settings` — show id, switched on, saved, quality, codec and specials (each empty when it
     follows the library), wishes, musts and forbidden (JSON lists).
   - `library_preferences` — library id, quality, codec, specials, wishes, musts, forbidden.
   - `EffectiveSettings` works out what applies to one show: its own quality, codec and specials, else
     the library's; the three tag lists joined; a tag the two put in different lists counts only in
     the show's list. No quality anywhere means the show searches nothing.
2. **The episodes searched for** are the aired episodes without a video file of shows switched on,
   saved and with a quality; season 0 only with specials on. `Ownership` ("one file on disk") no longer
   decides this; the switch does.
3. **Release names per run.** The four name sources' feeds (PreDB, srrDB, PreDB.net, SceneSource),
   read at once at the start of a run; a name is taken for an episode by its show and its `SxxEyy`.
   An episode no feed name was taken for is looked up in each source's search (backfill). A name with
   no episode number is dropped. Nyaa is not a name source. The name pool is gone: feeds are read every
   run and backfill asks the sources directly.
4. **Judging names** against `EffectiveSettings`: resolution is the quality, codec is the codec, every
   must, no forbidden. Refused names go to Skipped and Activity with their reason. Passing names are
   grouped by the number of wishes they carry, most first.
5. **The indexer round, per wish group.** First-choice indexers one after another in their order (tv:
   TorrentBay, LimeTorrents; anime: Nyaa, TorrentBay, LimeTorrents), then every other enabled indexer
   at once. Per indexer per name: exact, then without punctuation. A row counts only when its title is
   the name (letters and digits in order; case, punctuation and the site's own tag set aside). Rows of
   one hash are one torrent with every tracker. The torrent on the most indexers wins; a tie goes to a
   first-choice indexer (Nyaa first for anime), then the one found first. A release or hash still
   refused takes no part. An unreadable winner yields to the next. An empty group hands over to the
   group with one wish fewer. A question already asked of an indexer in this run is not asked again.
6. **A challenge twice.** A question to a site that does not answer or stays behind a challenge is
   asked at most twice, each after solving the challenge again; then the site sits out the rest of the
   run and the Sources page says why. Today's `ChallengeAwareFetch` solves once and retries once, and
   gives a timeout no second go.
7. **Only the winner** is offered to the client.

## What goes

The plugin's own search terms (`Show SxxEyy`, the season rungs, the absolute form, the quality added
to a source query), English only, require codec tag, the global `Profile` and its fields on the
Settings page, season packs as copies, the two-hash race (new grabs get no folder of their own; the
`grabs.folder` column stays for downloads already in one), ranking on seeders and site priority,
merging by name, the name pool, the separate Shows page, and "allow anyway" on Skipped.

## The pages

The contract offers forms with text, select and toggle fields, and tables with row buttons that post
to the plugin; it has no toggle or tag field inside a table row, and no encoding profile on
`PluginLibrary`. So:

- **Overview `/`** — the run's status line with Run and Stop; per library a preferences line with an
  Edit button and a table of its shows (show, year, missing, quality, codec, tags, state), a
  switch-on/off button and a settings button per row, 50 rows per page, switched-on shows first and
  each group alphabetical.
- **`/shows/{id}`** — one form, one Save: on/off, quality, codec and specials (each with "follow the
  library"), and for wishes, musts and forbidden a comma list and an append-one-tag field; below it
  what applies with the library counted in.
- **`/libraries/{id}`** — the same form without "follow the library".
- **Activity** — what the dashboard shows during a run today moves here.
- **Queue** — the episodes searched for, with "search now". **Skipped** — refused names with show,
  episode and reason, no allow button. **Settings** — folders, the run interval (15 min, 30 min, 1 h,
  6 h, 12 h, daily), the client, private trackers, advanced.

## Moving over

On the first start after the update `Profile` is dropped from `config.json` without being carried
over, every show is off, and nothing new is searched until the owner switches shows on, sets them, and
saves, with a quality on the show or its library. A download already running for a show that is not
switched on, saved and with a quality is cancelled and its bytes deleted, unless its episode is already
staged — the owner's answer of 15 September 2026, in `show-list.md`.
A library's quality comes from its folders' encoding profile once the server offers it (`S13-10`
files that issue); until then the owner sets it per library.

## Testing

Tests first, each seen to fail. Sources and indexers are tested against real captured pages in
`tests/fixtures/`; fresh captures are taken for the four feeds, the four sources' search and every
indexer that takes part. Every rule in `docs/specs/` gets a test that fails when the rule is deleted.
Tests that pin what goes are removed with it; a test whose rule only changes shape is rewritten. The
whole is seen working on beast-unit before `S13-12` releases it as v0.6.0.

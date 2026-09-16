# Torrent plugin specs

The plugin's requirements sheet: what the plugin does and requires, one page per feature. Every
entry is a requirement the owner has set.

## Rules

The rules are those of the NoMercy specs ledger (`NoMercy-Entertainment/nomercy-specs`), with one
difference: an entry here is the owner's requirement, whether or not the plugin meets it yet. A
requirement goes into the ledger once it exists in code, a test proves it, and it has been confirmed
by hand against a real running server.

- A page is `docs/specs/<feature>.md`, one directory deep, `<feature>` in kebab-case.
- A page opens with a `#` title naming the feature, followed directly by `##` sections.
- A section holds only plain, present-tense statements of what the plugin does, each one testable by
  a reader from the sentence alone.
- A page states the fact, not a prohibition, and needs no "must".
- A page carries no history, no intention, no explanation of why, no citation of a test or file, no
  progress count and no note about this folder's own process.
- A page names a role generically ("the server", "the owner"), never a person, a machine or a path on
  one.
- A page points at a rule that already has a home in `docs/` or `CLAUDE.md` rather than restating it.

`TheSpecSheetKeepsTheLedgerRulesTests` holds every page to the placement, the structure and the
ledger's banned-phrase list on every `dotnet test`.

## Pages

- [`show-list.md`](show-list.md) — the overview page: which shows and anime download, and their settings
- [`release-names.md`](release-names.md) — reading the sources, and choosing the release name for an episode
- [`indexer-search.md`](indexer-search.md) — searching the indexers, merging by hash, and the torrent that wins
- [`pages.md`](pages.md) — the Activity, Queue and Settings pages around the show list, and refusals
- [`run.md`](run.md) — what starts a run, the order of its work, and what happens when a site fails

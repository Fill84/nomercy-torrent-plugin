-- Nothing about a refused release name is written down any more, on the owner's
-- word of 16 September 2026 (docs/specs/pages.md). A run says what it refuses
-- while it runs, on the Activity page.
--
-- Their history held 5,851 refusals against 219 lines of everything else, and
-- every one of them was a name a show's settings refused before an indexer was
-- asked — noise the owner already understands. The Skipped page that read them
-- is gone with them.
DELETE FROM history WHERE event = 'skipped';

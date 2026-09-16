-- English only, back as a setting per show and per library, on the owner's word
-- of 16 September 2026 (docs/specs/show-list.md). It went with the global
-- profile in S13-09 and nothing replaced it, so a release in another language
-- was refused only where the owner had thought to forbid the word themselves.
--
-- NULL on a show means it follows its library, exactly as quality, codec and
-- specials do. On a library it is a plain flag, off until the owner turns it on.
ALTER TABLE show_settings ADD COLUMN english_only INTEGER;

ALTER TABLE library_preferences ADD COLUMN english_only INTEGER NOT NULL DEFAULT 0;

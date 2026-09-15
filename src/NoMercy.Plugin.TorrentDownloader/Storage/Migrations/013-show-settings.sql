-- Settings per show and per library, the owner's requirements of 15 September
-- 2026 (docs/specs/show-list.md). One global profile became one row per show and
-- one per library.
--
-- A show with no row is off and not saved, so it searches nothing: that is the
-- state every show starts in. quality, codec and specials are NULL while a show
-- follows its library, which is why they are nullable here and not on the
-- library. The tag lists are JSON arrays of the owner's words, in the order typed.
--
-- IF NOT EXISTS because StoreTests rewinds PRAGMA user_version to re-run an older
-- migration against a database this one has already run on.
CREATE TABLE IF NOT EXISTS show_settings (
    show_id     INTEGER PRIMARY KEY,
    switched_on INTEGER NOT NULL DEFAULT 0,
    saved       INTEGER NOT NULL DEFAULT 0,
    quality     TEXT,
    codec       TEXT,
    specials    INTEGER,
    wishes      TEXT NOT NULL DEFAULT '[]',
    musts       TEXT NOT NULL DEFAULT '[]',
    forbidden   TEXT NOT NULL DEFAULT '[]'
);

CREATE TABLE IF NOT EXISTS library_preferences (
    library_id TEXT PRIMARY KEY,
    quality    TEXT,
    codec      TEXT NOT NULL DEFAULT 'any',
    specials   INTEGER NOT NULL DEFAULT 0,
    wishes     TEXT NOT NULL DEFAULT '[]',
    musts      TEXT NOT NULL DEFAULT '[]',
    forbidden  TEXT NOT NULL DEFAULT '[]'
);

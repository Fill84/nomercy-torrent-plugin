-- The plugin's own store. docs/04-domain.md § Storage schema.
--
-- Every table here is the plugin's own bookkeeping. Nothing the media server
-- knows is stored as fact: `episodes` is derived from the library on every
-- maintenance pass and is never authoritative.

CREATE TABLE episodes (
    show_id        INTEGER NOT NULL,
    season         INTEGER NOT NULL,
    episode        INTEGER NOT NULL,
    show_title     TEXT    NOT NULL,
    show_year      INTEGER NULL,
    library_type   TEXT    NOT NULL,          -- tv | anime
    absolute       INTEGER NULL,              -- anime only
    episode_title  TEXT    NULL,
    air_date       TEXT    NULL,
    state          TEXT    NOT NULL,          -- notaired | missing | unavailable
    attempts       INTEGER NOT NULL DEFAULT 0,
    last_search_at TEXT    NULL,
    PRIMARY KEY (show_id, season, episode)
);

-- The queue is read as "what is missing, least recently tried first".
CREATE INDEX episodes_state ON episodes (state, last_search_at);

CREATE TABLE grabs (
    id            INTEGER PRIMARY KEY,
    show_id       INTEGER NOT NULL,
    season        INTEGER NOT NULL,
    episode       INTEGER NOT NULL,
    release_title TEXT    NOT NULL,
    info_hash     TEXT    NULL,
    source        TEXT    NOT NULL,
    magnet        TEXT    NULL,
    grabbed_at    TEXT    NOT NULL,
    state         TEXT    NOT NULL,           -- grabbed | downloading | done | failed | paused
    covers        TEXT    NOT NULL            -- json array of season/episode this answers for
);

-- Copies of one release are merged by info hash, so this is how a transfer is
-- found again from what the wire reports.
CREATE INDEX grabs_hash ON grabs (info_hash);

CREATE TABLE source_reports (
    name        TEXT PRIMARY KEY,
    at          TEXT NOT NULL,
    rows        INTEGER NOT NULL,
    refusal     TEXT NULL,
    duration_ms INTEGER NOT NULL
);

CREATE TABLE blacklist (
    key    TEXT PRIMARY KEY,                  -- normalised title, or info hash
    reason TEXT NOT NULL,
    at     TEXT NOT NULL,
    until  TEXT NULL
);

CREATE TABLE history (
    id            INTEGER PRIMARY KEY,
    at            TEXT NOT NULL,
    event         TEXT NOT NULL,              -- grabbed | skipped | failed | dispatched | allowed
    show_id       INTEGER NULL,
    season        INTEGER NULL,
    episode       INTEGER NULL,
    show_title    TEXT NULL,
    release_title TEXT NULL,
    source        TEXT NULL,
    detail        TEXT NULL
);

CREATE INDEX history_at ON history (at DESC);

CREATE TABLE name_pool (
    normalised TEXT NOT NULL,                 -- show+slot key
    title      TEXT NOT NULL,
    source     TEXT NOT NULL,
    seen_at    TEXT NOT NULL,
    PRIMARY KEY (normalised, title)
);

CREATE INDEX name_pool_seen ON name_pool (seen_at);

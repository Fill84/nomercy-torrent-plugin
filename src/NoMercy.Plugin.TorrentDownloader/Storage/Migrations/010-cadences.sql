-- The plugin's own clock, added S12-05: the host reads IScheduledTaskPlugin.Jobs
-- only when the plugin is installed, hot-swapped or enabled, so a saved cadence
-- has to take effect without waiting for one of those. One row per cadence
-- name, holding when it last finished. A cadence with no row has never run and
-- is due at once, which is the right answer on a fresh install.
--
-- This is migration 010 and not an edit to 009, because PRAGMA user_version
-- carries the number of the last migration that ran: editing 009 would never
-- create this table on any server that already recorded that version.
--
-- Corrected 12 September 2026: this said 009 "already ran on the owner's
-- server". It had not. beast-unit's torrent-downloader.db was at
-- user_version=8, read straight out of the file header, so 009 and 010 both
-- run on the first start after the next deploy. The reason above holds either
-- way and is the real one; the claim about the owner's server was wrong and
-- would have sent the next reader looking for a state that was never there.
--
-- IF NOT EXISTS because StoreTests.AnEpisodeGivenUpOnComesBackAsMissing rolls
-- PRAGMA user_version back to isolate migration 009 on a database this
-- migration has already run against once — a real server only ever runs
-- migrations forward, but a rewound version must not fail on a table that is
-- already there.
CREATE TABLE IF NOT EXISTS cadences (
    name             TEXT PRIMARY KEY,
    last_finished_at TEXT NOT NULL
);

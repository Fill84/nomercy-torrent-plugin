-- The plugin's own clock, added S12-05: the host reads IScheduledTaskPlugin.Jobs
-- only when the plugin is installed, hot-swapped or enabled, so a saved cadence
-- has to take effect without waiting for one of those. One row per cadence
-- name, holding when it last finished. A cadence with no row has never run and
-- is due at once, which is the right answer on a fresh install.
--
-- This is migration 010, not 009: 009 already shipped and already ran on the
-- owner's server, and PRAGMA user_version carries the number of the last
-- migration that ran. Editing 009 now would never create this table on a
-- server that already has that version recorded.
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

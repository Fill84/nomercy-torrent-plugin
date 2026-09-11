-- How each search run ended, so the dashboard can say it after a restart. It
-- used to be a field in memory: the server was restarted on 11 September 2026
-- and the dashboard said "never run" beside a Running badge, for a plugin that
-- had run a dozen times that night.
CREATE TABLE runs (
    started_at TEXT NOT NULL,
    ended_at   TEXT NOT NULL,
    outcome    TEXT NOT NULL
);

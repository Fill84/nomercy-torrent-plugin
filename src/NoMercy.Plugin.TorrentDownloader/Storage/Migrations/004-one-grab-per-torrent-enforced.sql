-- One torrent is one grab, enforced rather than tidied up.
--
-- 003 deleted the duplicates and said the cycle no longer made them. It does.
-- A grab is recorded for each episode the cycle decided, so anything deciding
-- the same episode twice in one pass — a show reached through two libraries,
-- two cadences arriving together — writes the same info hash twice, and the
-- index on the hash was not unique, so the table took it.
--
-- That left the rule living in cleanups: 003 once, and the maintenance cadence
-- at every start. Between two of those the Downloads page showed each release
-- twice. On 25 August 2026 three duplicates were cleared at a start and three
-- more were on the page the same evening.
--
-- The oldest row of each hash survives, which is the rule 003 already chose:
-- its grabbed_at is when the torrent was really taken on.
DELETE FROM grabs
WHERE info_hash IS NOT NULL
  AND id NOT IN (SELECT MIN(id) FROM grabs WHERE info_hash IS NOT NULL GROUP BY info_hash);

DROP INDEX IF EXISTS grabs_hash;

-- Unique, and still the index the hash is looked up by. A row with no hash is
-- a decision nothing was handed over, and SQLite counts NULLs as distinct in a
-- unique index, so any number of those remain allowed.
CREATE UNIQUE INDEX grabs_hash ON grabs (info_hash);

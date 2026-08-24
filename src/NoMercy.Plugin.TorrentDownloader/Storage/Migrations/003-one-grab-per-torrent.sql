-- One row per torrent.
--
-- Every cycle recorded a fresh grab for an episode it was already downloading,
-- because an episode stays missing until the library has a file for it. So one
-- release ended up with eight rows under one info hash, and every step that
-- walked grabs walked all eight: eight encode jobs for one file, on every tick.
--
-- The oldest row of each hash survives, because it is the one whose grabbed_at
-- says when the torrent was really taken on. The cycle no longer makes these,
-- so this runs once and finds nothing afterwards.
DELETE FROM grabs
WHERE info_hash IS NOT NULL
  AND id NOT IN (SELECT MIN(id) FROM grabs WHERE info_hash IS NOT NULL GROUP BY info_hash);

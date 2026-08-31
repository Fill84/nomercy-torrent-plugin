-- Every refusal was written for ever, whatever it was about. A swarm that did
-- not answer on one evening refused that release permanently: South Park S15E12
-- 1080p HMAX CtrlHD was blacklisted on 25 August 2026 for a metadata timeout,
-- and on 31 August the same release sat on TorrentBay with fifty seeders while
-- the plugin would not look at it and the owner watched it settle for a 720p.
--
-- Only the torrent's own contents are a reason that lasts: nothing will ever put
-- a video file into a release that has none. Every other refusal written before
-- the plugin could tell the two apart is set to a time already past, so those
-- releases are searchable again from the next cycle rather than never.
UPDATE blacklist
SET until = '2000-01-01T00:00:00.0000000+00:00'
WHERE until IS NULL
  AND reason NOT LIKE 'There is no video file%';

-- One release under two hashes is taken under both, and the first to finish is
-- kept: the owner's decision of 11 September 2026. Both torrents carry one name
-- and a torrent writes under its own name in the folder it is given, so the
-- second is given a folder of its own — and that has to be written down, or it
-- is staged from the wrong place and added back into the wrong one after a
-- restart. Null is the folder every other torrent uses.
ALTER TABLE grabs ADD COLUMN folder TEXT;

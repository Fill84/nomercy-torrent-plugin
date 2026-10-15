-- A wrong episode the plugin delivered is replaced on its own: the owner's rule of 15 October 2026.
--
-- On that day South Park S29E03 was delivered from "South Park S29E03 1080p HDTV DD 2.0 x264-ViETNAM", a
-- capture with the intro missing that no name source names, found by asking the indexers for the show and
-- episode three hours before any name source named a real release of it.
--
-- grabs.named: whether the release is a name a name source gave. 1 or 0 from this version on; null for a
-- grab recorded before, until it has been checked once against the name sources' search. A show's done
-- grab whose release is not named marks its episode to be replaced.
--
-- grabs.replaces: the info hash of the delivery a grab replaces, null for a grab that replaces nothing.
--
-- grabs.recycled_at: when the encoded files of that delivery went to the server's recycle area. Written
-- once, before the encode is asked for, so a later look never recycles what the new encode is writing.
ALTER TABLE grabs ADD COLUMN named INTEGER;
ALTER TABLE grabs ADD COLUMN replaces TEXT;
ALTER TABLE grabs ADD COLUMN recycled_at TEXT;

-- episodes.state gains 'replace': the library has a file, and it is one the plugin delivered from a release
-- no name source names. Not missing, so the overview does not count it; searched for by release name all the
-- same. The delivery it is to be replaced over is kept beside it, for the grab that replaces it and for the
-- Queue page.
ALTER TABLE episodes ADD COLUMN replaces_hash TEXT;
ALTER TABLE episodes ADD COLUMN replaces_release TEXT;

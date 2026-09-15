-- The name pool is gone (docs/specs/release-names.md, S13-05): every run reads the name sources' feeds
-- and asks their searches directly, so nothing is kept between runs. IF EXISTS, because the store's
-- tests rewind the version and run the later migrations again.
DROP INDEX IF EXISTS name_pool_seen;
DROP TABLE IF EXISTS name_pool;

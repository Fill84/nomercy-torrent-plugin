# Which release is the right one

The rules that decide whether a candidate release is the episode we wanted. Read out of
`f:/DevProjects/torrent-feed/matcher.py` on 11 August 2026 rather than paraphrased, because
the owner asked for "the same as torrent-feed" and a paraphrase is how two systems that are
supposed to agree stop agreeing.

**Everything here is per show**, with the general settings as the default a show inherits
until it says otherwise.

**Deliberately not per show:** maximum size and minimum seeders. The owner said so; both
stay one global setting each.

## The rules, in the order they are applied

1. **The show name must lead the title.** Accepted in exactly two positions: leading the
   title with at most a trailing year or country code (`Lucky 2026 S01E02`,
   `Big Brother US S28E08`), or ending exactly where the episode marker begins
   (`Special Ops Lioness S02E01`). Everywhere else is refused - this is what stops `Lucky`
   taking `Lucky Hank` or `We.Were.the.Lucky.Ones`.

2. **Exclude terms**, per show plus a global list. Normalised substring match: case and
   punctuation are stripped from both sides before comparing, so `HiggsBoson` catches
   `H1ggsBoson`-style spacing and `MULTI.1080p` matches a bare `multi`.

3. **`english_only`** (default on). Refused when *any* foreign-audio marker is present,
   **even alongside an English tag** - `ITA.ENG`, `FR.ENG` and `MULTI` multi-audio releases
   are all refused. Also refused on foreign episode numbering with no language tag:
   `Cap.101`, `capitulo`, `episodio`, `folge`, `odcinek`, `staffel`, `seizoen`, `saison`.
   The marker list is curated to avoid codes that are English substrings - `IT`, `ES` and
   `DE` are deliberately left out.

4. **`codec`**: `h264` | `h265` | `any`, default `h264`.
   - `h264` requires an explicit `x264` / `h264` / `H.264` / `H 264` / `avc` tag. **An
     untagged release is refused**, rather than passing as "at least it is not HEVC" -
     an untagged rip is exactly where an unwanted codec hides.
   - `h265` requires `x265` / `h265` / `H.265` / `H 265` / `hevc`.
   - The separator between letter and number may be a dot, a space, or nothing: the sites
     render scene dots as spaces, so `H.265` arrives as `H 265`.

5. **`quality`**, default `1080p`. A requirement, not a ceiling: normalised substring of
   the title, and a release that does not carry it is refused.

6. **Episode number must parse.** `s01e02`, `1x02`, `season 1 episode 2`.

7. **Season packs** refused unless `allow_season_packs` (default off).

8. **`from_season`**: refuse a season below it. Null means any.

9. **Score is the seeder count.** The best candidate is the most seeded one.

## Why this lives in the repository

Two of these were got wrong in production and cost real downloads: quality was read as a
ceiling, so a 720p copy scored well enough to win, and codec was read as "not HEVC", so
untagged releases arrived as x265. Both were written down here only after the fact. The
list is the contract; the tests below it name the case each rule exists for.

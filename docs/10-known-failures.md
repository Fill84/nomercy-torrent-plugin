# Known failures, and the test each one demands

Every one of these shipped in 0.3.x and was invisible. Grouped by what made them invisible, because
that is the property to design against.

**No slice is done until the tests in its rows exist and have been seen to fail first.**

## A. Category errors — asking a thing a question it cannot answer

| # | What happened | Why it was invisible | Test |
| --- | --- | --- | --- |
| A1 | **The profile asked a release *name* how many seeders it has.** Every announcement was refused for having nought, the resolver was never reached, and **not one indexer was ever asked**. `MinSeeders` is floored at 1, so no configuration worked. | Names were found for every episode. The log said "searched 24 episodes, found nothing worth taking". | A name with no torrent is not judged on seeders; the resolved copy is, and a copy below the minimum is refused with a history line naming the site and the count. |
| A2 | **An RSS feed was put in the search set** and asked a question per episode — forty identical requests per cycle, each answering with the newest twenty posts. | Every request succeeded. | A feed with no search address is read whole and never asked a query. |
| A3 | **Indexers were searched with `Silo S03E06`** instead of the full release name. | It sometimes worked, which is worse. | The find stage is asked the full release name; a query that is not a full name is a bug. |
| A4 | **Backfill used the indexers' search** instead of the feeds' and name databases'. | Results came back. | Backfill resolves names through feeds and name databases only. |

## B. A rule applied where it cannot be true

| # | What happened | Why it was invisible | Test |
| --- | --- | --- | --- |
| B1 | **`Unavailable` was permanent.** The query filtered it out and the refresh preserved it. | The queue just got shorter. | A maintenance pass re-derives state from the library; an unavailable episode with a new release becomes missing again. |
| B2 | **A failed download burned a search attempt.** Three failed grabs exhausted the episode. | Attempts went up, which looked like work. | A grab that fails does not count as a search attempt. |
| B3 | **A permission refusal counted as the site failing.** Three attempts parked the source fifteen minutes, and it stayed parked after the owner approved the host. | The message said "parked after repeated failures". | A refusal naming the host gate earns no failure, no backoff, no parking; a site that genuinely keeps failing still parks. |
| B4 | **Ranking was inverted** — `.ThenBy` on indexer priority picked the worst-rated site, and a test enshrined it. | It always returned something. | Between two acceptable copies the higher-priority indexer wins, asserted with distinct priorities. |
| B5 | **Ended shows were skipped.** 0.3.4 refused to search a show whose status was not "still going". | It looked like a sensible saving. | Every show in a tv or anime library is searched, whatever its status. Backfill is the point. |

## C. Plumbing that silently went nowhere

| # | What happened | Why it was invisible | Test |
| --- | --- | --- | --- |
| C1 | **`sources.json` read from `AppContext.BaseDirectory`** — the server's folder. Never found; silently fell back to three compiled-in sources. | Seventeen became three and nothing said so. | Read from the assembly's own folder; a missing file is logged; a test asserts more than ten sources load. |
| C2 | **Network grants requested only for the owner's own indexers** while the pipeline searched the shipped catalogue too. On a default install **no host was ever requested**. | The refusal reads like the site refusing us. | The runtime request covers **every source the pipeline will ask** — the shipped catalogue and the owner's own — search addresses included. See the note below: this shipped a second time. |
| C3 | **`DetailUrl` was written and read nowhere.** TorrentBay produced rows for weeks and zero downloads. | Rows appeared on the sources page. | A row with no magnet is followed to its own page and produces one. |
| C4 | **Two readers missing from the registry**; both fell through to the generic reader. | The generic reader returns rows on some pages. | Every reader name the catalogue uses resolves to a non-generic reader. |
| C5 | **Chrome re-downloaded on every server start** — 150 MB and a minute, because the check looked in the wrong place. | It logged it as news. | A second start finds the browser and does not fetch again. |
| C6 | **`GetShowsAsync(null)` returns every show in every library.** It only filters when a library id is passed. | Shows from libraries this plugin has no business in would be searched. | Libraries are enumerated and only `tv` and `anime` are read; a `movie` library's shows never appear. |
| C7 | **`HaveEpisodeCount` is zero for shows with hundreds of episodes on disk.** | A show with everything looks like a show with nothing. | Presence is derived from each episode's own `HasFile`, never from the show's count. |

**C2 shipped twice, and the second time it was this row that allowed it.** The fix column read
"shipped hosts are in the manifest", and the plugin was written to that: it declared seventeen hosts
in `plugin.json` and asked at runtime only for `settings.Indexers`. A manifest declares what a plugin
*may* ask for. It is not a grant, and the owner cannot say yes to a question nobody put to them — so
on 22 August 2026, on a default install with no indexers of the owner's own, not one request was
made, the dashboard had nothing to show, and all seventeen sources refused themselves. The plugin ran
its cadence and reported that the sites had refused it.

Anything that reaches the network is asked for at runtime, every source, or it is not asked for at
all.

## D. The browser and the wire

| # | What happened | Why it was invisible | Test |
| --- | --- | --- | --- |
| D1 | **JSON and XML through the browser came back as Chrome's viewer.** Every JSON source returned `[]`; an XML feed reported "malformed feed XML: The 'meta' start tag on line 1". | An empty array is a valid answer. | A JSON endpoint fetched through the solver returns the raw body; the fixture is the viewer markup. |
| D2 | **Polling for a cleared challenge threw `Execution Context was destroyed`** four times in one run. | Logged far away as a source failure. | A navigation during the poll is not a failure; the poll continues. |
| D3 | **A browser window opened on the owner's desktop.** | Only visible to somebody at the machine. | The hidden stage is created before the browser starts; where none can be hidden, gated sources are skipped and logged. |
| D4 | **`[GeneratedRegex]` returned zero matches** where the identical inline `Regex` returned fifty. | Zero rows is what a site with nothing looks like. | The TorrentBay reader is tested against the real capture with a non-zero row count. Prefer `static readonly Regex` in readers. |

## E. Reader-versus-page mismatches

| # | What happened | Test |
| --- | --- | --- |
| E1 | **TorrentFunk writes bare attributes** (`class=tv3`); the reader asked for quoted ones. Zero rows from a page whose heading said "We have 1 for you". | Read against the real capture; assert the row and its detail address. |
| E2 | **TorrentFunk's title is split by a span** colouring the group, so the group was lost. | The parsed title from the capture includes the group. |
| E3 | **A plus in a path is a plus.** TorrentGalaxy answered nothing for a release it has sixteen copies of. 1337x searches from its path too and wants the plus. | `spaced` sends `%20`, the default sends `+`, both asserted on the URL. |
| E4 | **A site answers a full release name with that release's own page**, not a listing — and the fallback written for it, "no rows, so take a magnet anywhere on the page", is unsafe. The capture of 22 August 2026 has no listing and one magnet, and that magnet is a wallpaper pack: the page's title would have gone out in front of a stranger's torrent. The source it was written for is gone. | A row is named by the torrent it points at, never by the page around it. |
| E5 | **Sites were declared dead too fast.** TorrentGalaxy, Torrentz2 and TorrentDownloads each worked once the right address and shape were found. | Every source has a fixture and a reader test with a non-zero row count. |
| E6 | **A dozen forty-hex strings on TorrentGalaxy are element ids**, not info hashes. | A bare hash is read only when the page has exactly one. |

## F. Time, cancellation and overlap

| # | What happened | Test |
| --- | --- | --- |
| F1 | **The Run button awaited the cycle inside the HTTP request**, so it held the caller's cancellation token. Twenty-nine minutes of work thrown away. | A run started with an already-cancelled token still runs; the endpoint answers before the work is done. |
| F2 | **The run lock was taken with the caller's token.** A zero wait cannot block, so it bought nothing and killed the run on the way in. | Covered by F1's test, which fails at exactly this line without the fix. |
| F3 | **No overlap protection.** A thirty-minute cycle against a five-minute cron is six concurrent searches. | A tick arriving while its own cadence runs is dropped and logged. |
| F4 | **A download that finished while the server was down was never noticed.** Completion was only seen while watching. | A torrent finished before start-up is staged and dispatched on the first transfers tick. |

## G. Reporting that was wrong about itself

| # | What happened | Test |
| --- | --- | --- |
| G1 | **An error said "search returned HTTP 429" without the address.** When the address was added it leaked the API key. | A refusal names the address, with key-ish parameters blanked out. |
| G2 | **The health check attributed one source's page to another** and **reported its own rate-limiting as a broken parser**. | The captured body is cleared between sources; a rate-limited source is retried once and reported distinctly. |
| G3 | **The UI showed raw parser output instead of profile-filtered results.** | Any page listing candidates renders what the profile accepted; a rejected codec never appears. |
| G4 | **The Downloads page showed nothing while grabs existed.** | A grab with no transfer yet renders a row saying so. |

## H. Tests that were worse than none

| # | What happened | Rule |
| --- | --- | --- |
| H1 | Every test covering the seeder fault **stubbed the profile out with a fake chooser** and passed throughout. | Chain tests use the real profile. A fake chooser is only for tests about plumbing, never about a decision. |
| H2 | **A test enshrined the inverted ranking.** | Every rule test must fail when the rule is deleted. Check it. |
| H3 | Parsers tested against hand-written samples avoided every real case: a show called *Greek* read as a Greek-language release, a diacritic tokenised into fragments, `[eztv.re]` appended to every title. | Parsers are tested against real captures only. |

## The standing check

Before finishing any slice: **if this stage silently did nothing, what would say so?** If the answer
is "the owner would eventually notice fewer downloads", it is not instrumented and the slice is not
done.

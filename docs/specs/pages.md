# The plugin's pages

## Tabs

- A page with two or more tables shows one table at a time, under a row of tabs with one tab per
  table. A page with one table has no tabs.
- A tab's label is its table's name, followed in brackets by how many rows the table holds where that
  count says something, as in "Looking (12)".
- The chosen tab is part of the page's address, so a reload and the browser's Back draw the same tab.
  An address that names no tab, or a tab the page does not have, draws the first tab.
- The chosen tab is marked the way the plugin's navigation marks the page being read, and the
  navigation marks the page a tab belongs to.
- The tabs are buttons in a row, as the navigation is: each is focusable on a television, and left
  and right move from one to the next.
- The overview page has a tab per library, at that library's page of shows.

## Sorting, filtering and paging

- Every table answers five keys in the page request's query: `sort`, `order`, `filter`, `page` and
  `pageSize`, whatever the case of the key. They apply to the table the page shows.
- `sort` names a column by its key and `order` is `asc` or `desc`, ascending when it is left out.
  A column of sizes, rates, durations, counts or times sorts by that value, not by the words in the
  cell; any other column sorts by its words, case set aside. A row whose value is not known comes
  after every row whose value is, in either order. Rows that sort the same keep the table's own order.
- `filter` keeps the rows where the words of a cell contain the filter text, case set aside. A
  row's buttons are not its words. When rows exist and none match, the table says that nothing
  matches the filter, rather than that it is empty.
- A sort key the table has no column for, the column of buttons, and an `order` that is neither
  `asc` nor `desc` leave the table in its own order. No key or value makes a page fail.
- Only the overview's table of a library's shows pages, 50 shows to a page. On it the rows are
  filtered, then sorted, then paged, so the second page continues the first page's order. A `page`
  in the query wins over the page in the address. A page past the last is the last page, and one
  before the first is the first.
- `pageSize` is honoured from 1 to the table's own 50. A size outside that, or one that is not a
  number, is the table's own 50.
- Under any of the keys the overview's paging line says the page, the number of pages and how many
  shows match, as in "page 2 of 3 · 120 shows", even when the shows fit on one page, and the overview
  draws no Previous or Next buttons. Without any of the keys, Previous and Next go to the address of
  the page before and the page after.
- A tab's count is the number of rows its table holds, whatever `filter` keeps.
- A table that does not page holds all its matching rows whatever `page` and `pageSize` say.
- A request with none of the keys draws every page exactly as it does without them.

## Long text in a table

- Long text in a cell never makes its table wider than the page.
- A long column holds a set number of characters, "…" included: on the overview a show's title 48 and
  its tags 48; a source's refusal 77; on the History page the release a line is about 60 and the reason
  77; on the Activity page a note 77, and in the table of what is in flight the episode 83 and what it
  waits on 77, and the counts of a stage 164; on the Downloads page the release 44 and the state 34.
- A stage's counts longer than their column are cut after the last whole count, the counts being joined
  by " · ", and "…".
- A refusal, a reason, a note or a download's state whose text is longer than its column, or holds a
  web address, shows the text's first sentence, up to the first web address, and "…". A first sentence
  longer than the cell is cut after the last whole word that fits.
- A release name, a show's title or a tag list longer than its column is cut after the last space, dash,
  underscore or dot that fits, and "…". A full stop in a release name is not the end of it. A dot after
  a single letter, as in "H.265", or with a digit before it and one digit after, as in "DD5.1", is
  inside a word; any other dot ends a word, as in "McGovern.S01E01.1080p". A text with nowhere to cut is
  cut where the cell ends.
- A size or a rate on the Downloads page never has more than three digits before its unit: from 1000 of
  a unit it is said in the next, below one of that unit with two decimals, as in "0.98 MB/s".
- A web address (http or https) is never in a cell. A host and port such as "(torrentz2.nz:443)" is not
  a web address.
- The whole text of each cell drawn short stands under the table as a line of its own, in the order of
  the rows. On a page with tabs the lines stand under the chosen tab's table. On the overview they stand
  above the paging line. On the Downloads page they stand under the field that takes a magnet, which
  stays directly under the table.
- A text that fits and holds no web address is the cell as it is, with no line under the table.
- The Activity page counts the downloads of the Download stage by their state, without the client's
  error.
- `filter` and `sort` read the whole text, so a row is found by a word that is only in the line under
  the table.

## Activity

- The Activity page shows what a run is doing: the count per stage, what is in flight, and for every
  episode being worked on which name source was asked and what it answered, which release name was
  refused and why, and every question to every indexer with the rows it returned.
- While a run is going those are three tabs: Stages, Now and Notes. The stages carry no count. With no
  run going the page holds what is in flight, and the notes while any are left.

## Queue

- The Queue page lists the episodes being searched for: the aired episodes without a video file of
  the shows and anime that are switched on with a quality, their own or their library's, and the
  episodes to be replaced (`run.md` § The order of a run).
- Every row on the Queue page carries a button that searches for that episode now.
- The page holds two lists, the episodes being looked for and the episodes waiting to air, as two
  tabs: Looking and Waiting to air.
- While an episode is to be replaced, a third tab, To be replaced, lists each one with the release it
  replaces: "to be replaced: <release> — waiting for a release the sources name". The episode is in
  Looking as well, in the order the run asks.

## Refused release names

- The plugin has no Skipped page, and nothing about a refused release name is stored.
- A refusal is said on the Activity page while the run is going, with the name and the reason, and
  goes when that episode is decided.

## Settings

- The settings page holds the folders, the run interval, the torrent client, the private trackers
  and the advanced settings.
- The run interval is chosen from every 15 minutes, every 30 minutes, every hour, every 6 hours,
  every 12 hours and once a day.
- At the foot of the page it says which version of the plugin drew it — the copy that is answering,
  not the one the catalogue installed, because an update without a restart can leave the two apart.

## Pages that stay as they are

- The Downloads, History and Sources pages show what they show, unchanged by the show list.
- The History page holds what became of a grab — decided, grabbed, dispatched, failed — and no
  refusals.
- The Downloads table has no column for where a download lands.
- A download's progress is the percentage of its size, as in "45.3% of 23.4 GB", with nothing beside it.
- A download's rate is the rate it downloads at. The rate it uploads at is not drawn.
- On the Downloads page the field that takes a magnet or a .torrent stands under the list of
  downloads, and the list scrolls within its own height rather than pushing the field down.
- The plugin has no separate Shows page. The overview page lists the shows.

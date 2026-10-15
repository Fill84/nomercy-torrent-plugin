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

## Activity

- The Activity page shows what a run is doing: the count per stage, what is in flight, and for every
  episode being worked on which name source was asked and what it answered, which release name was
  refused and why, and every question to every indexer with the rows it returned.
- While a run is going those are three tabs: Stages, Now and Notes. The stages carry no count. With no
  run going the page holds what is in flight, and the notes while any are left.

## Queue

- The Queue page lists the episodes being searched for: the aired episodes without a video file of
  the shows and anime that are switched on with a quality, their own or their library's.
- Every row on the Queue page carries a button that searches for that episode now.
- The page holds two lists, the episodes being looked for and the episodes waiting to air, as two
  tabs: Looking and Waiting to air.

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
- On the Downloads page the field that takes a magnet or a .torrent stands under the list of
  downloads, and the list scrolls within its own height rather than pushing the field down.
- The plugin has no separate Shows page. The overview page lists the shows.

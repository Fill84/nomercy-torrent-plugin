# Where the information comes from

The media server is the only source of truth about what exists and what is on disk. The plugin
derives everything and stores none of it as fact.

Read through `IPluginContext.Library`, which is `IPluginLibraryQuery`. On the server that is
`src/NoMercy.Data/Plugins/PluginLibraryQuery.cs` — read-only EF projections, `AsNoTracking`
throughout, into DTOs owned by the plugin contract.

## The four calls, and what each really returns

```csharp
Task<IReadOnlyList<PluginLibrary>>        GetLibrariesAsync(CancellationToken)
Task<IReadOnlyList<PluginLibraryShow>>    GetShowsAsync(string? libraryId, CancellationToken)
Task<IReadOnlyList<PluginLibraryEpisode>> GetEpisodesAsync(int showId, CancellationToken)
Task<IReadOnlyList<PluginLibraryFile>>    GetShowFilesAsync(int showId, CancellationToken)
```

```csharp
record PluginLibrary(string Id, string Title, string Type);

record PluginLibraryShow(int Id, string Title, int? Year, string LibraryId, string? Folder,
                         int EpisodeCount, int HaveEpisodeCount);

record PluginLibraryEpisode(int ShowId, int SeasonNumber, int EpisodeNumber, string? Title,
                            DateTime? AirDate, bool HasFile);

record PluginLibraryFile(int ShowId, int? SeasonNumber, int? EpisodeNumber, string Path,
                         string Quality);
```

`Year` is the show's first air date's year. `Folder` is relative to the library root and is null
when the show has none — and a blank one counts as none too, because an empty string is a folder
name that resolves to the library root. An episode's `Title` is nullable as well.

## Media type

Whether something is television or anime is the server's own classification, not this plugin's.
`MediaTypeClassifier.ClassifyAsync(title, year)`
(`src/NoMercy.MediaProcessing/Shows/MediaTypeClassifier.cs`) answers `"tv"` or `"anime"`, backed by
Kitsu. `InboxClassifier` calls it for every episodic file and its comment says why: whether a file
belongs in the anime or the tv library "is decided by the shared Kitsu-backed classifier, never by
filename shape alone".

**A show is already filed under its media type.** The server put it in the library that matches, so
the media type of a show this plugin reads is the type of the library it sits in — available as
`PluginLibrary.Type` from `GetLibrariesAsync`. The plugin classifies nothing and guesses nothing: it
reads what the server already decided.

`Library.Type` is a plain, indexed string column
(`src/NoMercy.Database/Models/Libraries/Library.cs`) with no enum behind it, so the plugin compares
case-insensitively and treats anything it does not recognise as out of scope.

**This plugin reads media types `tv` and `anime`, and nothing else.** Films are out of scope;
`GetMoviesAsync` is never called.

### The episode goes back to the library of its own media type

A downloaded episode is dispatched to the library the show came from — `PluginLibraryShow.LibraryId`
— so an anime episode lands in the anime library and a television episode in the tv library. The
plugin never picks a library; it uses the show's own. See `docs/09-host-contract.md`.

## How the missing list is derived

```
for each library whose media type is "tv" or "anime"
    for each show in GetShowsAsync(library.Id)
        skip the show when Folder is null            ← no folder means nowhere to download to
        skip the show when no episode HasFile        ← not a show the owner has; see below
        for each episode in GetEpisodesAsync(show.Id)
            skip season 0 unless IncludeSpecials
            missing when   HasFile is false
                     and   AirDate is not null and in the past
```

That is the whole rule. There is no follow list, no subscription, no opt-in and no status check, and
an episode that aired two years ago counts exactly as much as one that aired last night.

### One episode on disk, or it is not the owner's show

**A show is the owner's when at least one of its episodes has a file, and only then.** It is taken
from the query behind the server's own library page
(`Episodes.Any(e => e.VideoFiles.Any(v => v.Folder != null))`), and it is written once, in
`Core/Pipeline/Ownership.cs`, where both the refresh and the transfers tick ask it.

**It was widened to every show in a library on 24 August 2026 and put back the same afternoon.**
Within the hour the plugin was on 479 grabs, 456 of them Family Guy — a show the owner has never
watched. The reasoning had been sound and the premise was false: a library row is not a show the
owner added. The server keeps rows for shows nobody asked for, in the same table, against the same
library id, with a folder and a full episode list, and nothing in such a row tells it apart from a
show they added. Having a file is the only thing that does.

`MaxSearchAttempts` does not save this. It bounds how long each episode is looked for; it does not
stop 456 of them being looked for at all.

**It is a workaround and it is known to be one.** A show just added has nothing on disk and is
therefore invisible to this plugin, which is exactly when it would be most use. That is a gap in the
host contract rather than something to work around here: media-server **#36** stops identification
importing shows on a guess, so a library row means the owner asked for it, and **#34** makes a newly
added show visible. When both land, library membership becomes the rule and this paragraph is
replaced by one sentence. Neither is this repository's to close.

### Three corrections, each measured

**`GetShowsAsync(null)` returns every show in every library.** It only filters when a library id is
passed. The plugin must enumerate libraries itself and call it per library, or it will try to
download episodes of films' shows and of libraries the owner never meant for this.

**`HaveEpisodeCount` is not usable.** It is the `Tv.HaveEpisodes` column, and on a real server it is
zero for shows with hundreds of episodes on disk. Whether a show has anything on the server is
derived from the episodes' own `HasFile`, which is `episode.VideoFiles.Any()` and is correct. Two
numbers that can disagree must never both be trusted; the one this plugin already uses to decide
"missing" is the one that also decides "present".

**There is no show status in the contract on `dev`.** `Tv.Status` and `Tv.InProduction` exist in the
database, but `PluginLibraryQuery` does not project them, and `PluginShowStatus` does not exist on
that branch. This plugin does not need them: an ended show is exactly the kind with gaps to fill,
and skipping it is the opposite of what backfill means.

### What `GetShowFilesAsync` is for

It gives the path and quality of every video file a show already has. Used for one thing: an episode
that is present but at a lower quality than the profile allows is **not** re-downloaded in 0.4.0.
The call is available and the upgrade decision is deliberately out of scope — noted here so nobody
wonders whether it was forgotten.

## Anime numbering

Anime releases are usually numbered from the start of the series, not the season: episode 13 of
season 2 is `- 137`. The library numbers by season. Both must be searchable.

```
absolute(show, season, episode) = episode + Σ episodeCount(show, s) for s in 1..season-1
```

Season 0 never counts, and a special is never given an absolute number of its own. The map is built
once per show per cycle from the episode list that was already fetched, so it costs no extra call.

Read the formula literally: it is the episode's **own number** plus the lengths of the seasons before
it, never its position in the list. Those agree only while the list is complete, and they part
company exactly when episodes are absent — which is the case this plugin exists for. An episode
already on disk still counts towards the offset, or a show would renumber itself every time
something downloaded.

A show in an `anime` library is therefore searched under both forms, pooled and judged together:

- `Show Title S02E13`
- `Show Title - 137`
- `Show Title 137`

## Show titles

The library's title and a scene title are not always the same string. Where a show's title is a
common word, it is searched under both its bare title and its title with year — `Sugar` and
`Sugar 2024` — pooled and judged together. Four shows in a real library need this: Lucky (2026),
Sugar (2024), Lioness (2023), Silo (2023).

## What is stored

Nothing from the library is stored as fact. The `episodes` table is a derived cache, rebuilt on every
maintenance pass, keeping only the plugin's own bookkeeping — how many times an episode has been
searched and when. A row for an episode the library no longer has is deleted.

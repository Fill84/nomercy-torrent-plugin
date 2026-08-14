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

record PluginLibraryShow(int Id, string Title, int? Year, string LibraryId, string Folder,
                         int EpisodeCount, int HaveEpisodeCount);

record PluginLibraryEpisode(int ShowId, int SeasonNumber, int EpisodeNumber, string Title,
                            DateTime? AirDate, bool HasFile);

record PluginLibraryFile(int ShowId, int? SeasonNumber, int? EpisodeNumber, string Path,
                         string Quality);
```

`Year` is the show's first air date's year. `Folder` is relative to the library root and is null
when the show has none.

## Library types

`PluginLibrary.Type` is the library's own type, set when the owner created it. The server uses
three: **`movie`**, **`tv`** and **`anime`**
(`src/NoMercy.Api/Controllers/V1/Dashboard/Media/RecommendationsController.cs`).

**This plugin reads libraries of type `tv` and `anime`, and nothing else.** Anime is a library type,
not a guess: there is no genre and no origin country in the plugin contract, and none is needed.
A show in an `anime` library is anime; a show in a `tv` library is television.

Films are out of scope. `GetMoviesAsync` is never called.

## How the missing list is derived

```
for each library where Type is "tv" or "anime"
    for each show in GetShowsAsync(library.Id)
        skip the show when Folder is null            ← no folder means nowhere to download to
        for each episode in GetEpisodesAsync(show.Id)
            skip season 0 unless IncludeSpecials
            missing when   HasFile is false
                     and   AirDate is not null and in the past
```

That is the whole rule. There is no follow list, no subscription, no opt-in and no status check.
Every show in those libraries is in scope, and an episode that aired two years ago counts exactly
as much as one that aired last night.

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

Season 0 never counts. The map is built once per show per cycle from the episode list that was
already fetched, so it costs no extra call.

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

Nothing from the library is stored as fact. The `wanted` table is a derived cache, rebuilt on every
maintenance pass, keeping only the plugin's own bookkeeping — how many times an episode has been
searched and when. A row for an episode the library no longer has is deleted.

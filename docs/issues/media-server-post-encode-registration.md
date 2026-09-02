# A finished encode is registered against whatever its filename parses to, not against the episode it was made for

Raised from `nomercy-torrent-refactor-plugin`, 2 September 2026. Everything below was read out of the
owner's own `media.db` and `run-*.jsonl` on `beast-unit`. Read against media-server **v0.1.482**;
every file named below is byte-identical to `v0.1.481`, so the line numbers hold for both.

**This repository is read-only to the plugin's author.** The issue is written so it can be handed
back and carried out in one pass without touching anything else.

## What happens

An encode dispatched for one episode is registered against a different episode, and is registered
twice.

South Park S15E12 on the owner's server:

| | |
| --- | --- |
| Episode the plugin dispatched for | `153823` — `Episodes` row `TvId=2190, SeasonNumber=15, EpisodeNumber=12`, title `1%` |
| What the encode job read back | `[VideoEncodeJob] Reconciliation: skipping preset '1080p regular' for 153823` |
| What the encoder wrote | `/South.Park.(1997)/South.Park.S15E12/South.Park.S15E12.1%.NoMercy.m3u8` |
| What `VideoFiles` ended up holding | **two** rows, both `EpisodeId = 153785` — `SeasonNumber=0, EpisodeNumber=12`, title `Chef Aid: Behind The Menu` |
| What episode `153823` holds | nothing |

So the id was right the whole way in, the output landed in the right folder under the right name, and
the row that was written points at the wrong episode.

**The plugin is not sending the wrong id.** `PluginLibraryQuery.GetEpisodesAsync` answers
`Id = episode.Id`, which for (15, 12) is `153823`; the plugin sends that verbatim to
`IPluginEncoder.EncodeAsync`; `PluginEncoder` puts it in `VideoEncodeJob.Id`; and the job's own log
line names `153823` back. There is no step in between where the plugin chooses or derives an id.

### What it costs

The episode never gains a file, so anything waiting on it waits for ever. The plugin waited six
hours, gave the episode back to the missing list and fetched the same release again. It also puts the
episode in the owner's library under season 0, where they will not find it.

## Why

`FileManager.FindFiles(int id, Library library)` — `FileManager.cs:121` — uses `id` **only** to
resolve which show and which folders to scan:

```csharp
public async Task<bool> FindFiles(int id, Library library)
{
    Id = id;
    await MediaType(id, library);          // resolves Show / Movie
    Folders = Paths(library, Movie, Show); // the folders to walk
    …
    ReResolveNames(library.Type);          // every file's name parsed again
```

The episode a file is attached to is then decided from the parsed filename, at
`FileManager.Storage.cs:150`:

```csharp
Episode? episode = await fileRepository.GetEpisode(Show?.Id, item);
…
EpisodeId = episode?.Id,                   // FileManager.Storage.cs:195
```

`item` is the scanned file with `item.Parsed` filled in by `ReResolveNames`
(`FileManager.cs:64`), which runs the filename through `_resolver.Resolve(...)`.

So the encode job knows exactly which episode it was made for and exactly which file it wrote, and
the registration throws both away and re-derives the episode from the name. For
`South.Park.S15E12.1%.NoMercy.m3u8` the resolver lands on season 0 episode 12 rather than season 15
episode 12 — the `1%` is the episode's own title and the resolver reads it as part of the numbering.

`VideoEncodeJob.ScanEncodedOutputWithRetryAsync` (`VideoEncodeJob.cs:1350`) is the caller. It already
holds `mediaId` and a `filterFileName`, and passes the id straight into `FindFiles`, where it is used
for folder resolution and nothing else.

### And the duplicate is a second, separate fault

`VideoFile` declares `[Index(nameof(Filename), nameof(HostFolder), IsUnique = true)]`
(`VideoFile.cs:20`). Both rows survived it because `HostFolder` differs:

```
01M1E0T6QTN6N5DPC5TDA94FPE | 153785 | Y:/nomercy/media/TV.Shows/TV.Shows/South.Park.(1997)/South.Park.S15E12
01M1E0VFFQZ7HZCE1NX5HX17DJ | 153785 | South.Park.(1997)/South.Park.S15E12
```

Same `Folder`, same `Filename`, same episode. One host-absolute path with a doubled `TV.Shows`
segment, one library-relative. The unique index is doing its job; the value it is keyed on is not
stable.

Both rows are written by the same line — `FileManager.Storage.cs:199`,
`HostFolder = hostFolder.Replace("\\", "/")` — on two separate runs. `hostFolder` is derived at
`:135` as `itemPath.Replace(fileName, "")`, over an `itemPath` that `:131` has just put through
`StoragePathHelpers.RebaseToFolderRoot(itemPath, folder.Path)`. So the value depends on what the
rebase produced that time, and it produced two different things for one file. `Folder` — `baseFolder`
at `:140`, anchored on the show's own folder name — was stable across both runs; only `HostFolder`
moved.

The doubled `TV.Shows/TV.Shows` in the first value is the tell: that rebase misfired at least once.

## Three fixes, in order of what they are worth

### 1. Register the encode's own output against the episode it was dispatched for

**The real fix.** Everything else here is about files whose provenance nobody knows; this one's is
known exactly.

- In the post-encode path only, the file the job just published is attached to `VideoEncodeJob.Id`
  directly, without consulting `item.Parsed` for the episode.
- The scan keeps doing what it does for every other file it finds. Nothing about the general
  filename-driven path changes: a file that arrived any other way is still resolved by name.
- Smallest shape that does it: give `FileManager` an optional "this file is this media id" hint —
  set alongside the existing `FilterFiles(filterFileName)` call, since that call already narrows the
  scan to the one output — and have `FileManager.Storage.cs:150` prefer that hint over
  `GetEpisode(Show?.Id, item)` when the scanned file is the filtered one.
- **Do not** change `GetEpisode`, the resolver, or `ReResolveNames` for this. They stay exactly as
  they are for every other caller.

Verification: dispatch an encode for an episode whose title defeats the parser — `1%` is a real one
already on the owner's disk — and assert the `VideoFiles` row carries the dispatched id.

### 2. Make `HostFolder` one value, so the unique index can do its work

- One canonical form. `hostFolder` at `FileManager.Storage.cs:135` is whatever
  `RebaseToFolderRoot` left behind that run; normalise it once, there, to one shape — either
  host-absolute or library-relative, but the same one every time — so `:199` cannot write two
  spellings of one folder.
- The doubled `TV.Shows/TV.Shows` says `RebaseToFolderRoot` itself can produce a path that has been
  rebased twice. Worth a look while you are in there; it is the likeliest source of the second
  spelling.
- Then the existing `(Filename, HostFolder)` unique index prevents the second row on its own; no new
  index is needed, and adding one would not have helped, because the values genuinely differ.
- Migration: the rows already written need normalising, or the unique index will keep tolerating the
  pairs already there. The owner has at least one such pair.
- **Do not** widen the index to include `EpisodeId`. That would make a file legitimately re-attached
  to a corrected episode insert a second row instead of moving.

Verification: run the post-encode registration twice for the same output and assert one row.

### 3. Only then, the resolver

Titles carry `%`, `#`, `:`, brackets and dots. `South.Park.S15E12.1%` is one case; the next odd title
is another. Worth improving on its own merits — a file that arrives with nothing but a name has
nothing else to go on — but it is symptom work: with (1) in place the encoder's own output never
depends on it.

**Do not** do (3) instead of (1). The parser will always lose some names, and the encode path has no
reason to be exposed to that.

## What the plugin does meanwhile

`S11-07` in the plugin: where the server says the job is finished and the episode still has no file,
the plugin looks at the show's files and treats a file whose own name carries that season and episode
as that episode. It stops the six-hour wait and the second download, and says so on the History page.

It is a workaround for exactly this issue and it is harmless once (1) lands. It does not repair the
`VideoFiles` row — the owner's library still shows the episode under season 0 until this is fixed.

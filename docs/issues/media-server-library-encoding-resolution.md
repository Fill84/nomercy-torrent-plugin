# plugins: `PluginLibrary` says nothing about how its folders are encoded, so a plugin cannot tell a library's resolution

**Filed as media-server [#56](https://github.com/NoMercy-Entertainment/nomercy-media-server/issues/56).**

Raised from `nomercy-torrent-refactor-plugin`, 15 September 2026. Read against media-server `dev` at
`d290b002d`; the contract, the preset models and the profile records were checked again on `master` at
`v0.1.504` and are unchanged there.

**This repository is read-only to the plugin's author.** The issue is written so it can be picked up and
carried out in one pass without touching anything else.

## What the plugin needs

The torrent plugin searches each show at one quality — one resolution, 2160p, 1080p, 720p or 480p. The
owner's requirement is that a library's quality is **the resolution of the encoding profile of the
library's folders**, whenever the server tells the plugin that profile, and only otherwise a value the
owner types in. A release taller than the library is encoded to is downloaded for bytes the encode
scales away, and a shorter one never reaches the top of the ladder, because `NeverUpscale` keeps it at its
own size.

Today the server tells a plugin nothing about it:

```csharp
// src/NoMercy.Plugins.Abstractions/IPluginLibraryQuery.cs:69
public record PluginLibrary(string Id, string Title, string Type);
```

built at `src/NoMercy.Data/Plugins/PluginLibraryQuery.cs:44` (and the same projection in
`PluginLibraryWriter.cs:53-59`) from `Libraries` alone. So the owner sets a quality per library in the
plugin by hand, and it can disagree with what the server will really encode to without anything saying so.

## Where the facts already live

| | |
| --- | --- |
| Library → folders | `FolderLibrary` (`Models/Libraries/FolderLibrary.cs:20`), PK (`FolderId`, `LibraryId`) |
| Folder → presets | `EncodingPresetFolder` (`Models/Media/EncodingPresetFolder.cs:20`): `PresetId`, `FolderId`, `IsDefault` — **a folder can link several presets**, and `VideoEncodeJob.cs:254-307` encodes to all of them |
| Library narrowing | `Library.EncodePresetId` (`Models/Libraries/Library.cs:52-54`): when set, auto-encode runs only that preset (`AutoEncodeSubscriber.cs:115,190`, `VideoEncodeJob.cs:259-268`) |
| The preset's outputs | `EncodingPreset.ProfileJson`, merged through the parent chain by `PresetResolver.Resolve` (`NoMercy.Encoder/Profiles/PresetResolver.cs:41`) into `EncodingProfile` |
| Resolution | `EncodingProfile.Video` (`VideoOutput.cs:16`: `int? Width, int? Height` — null is the source's own size) and `EncodingProfile.Ladder` (`LadderConfig.cs:14`: `Mode` `Auto` or `Manual`, `Rungs`, `AutoConfig.Tiers`) |

Two facts that decide the shape of the answer:

- **A ladder has a ceiling, not one resolution.** The default auto-encode preset,
  `H.264 Streaming (Universal)` (`BuiltinPresets.cs:32,69-76`), is an `Auto` ladder over
  `LadderTiers.YouTube`, 144p to 2160p, with `NeverUpscale = true` (`BuiltinPresets.cs:612-640`). The
  rungs a given file gets depend on that file (`LadderGenerator.cs:66`); what the preset says before any
  file is seen is its highest tier. A `Manual` ladder's ceiling is its highest rung, and a preset with no
  ladder has the one height of `Video`, or none when it keeps the source's size.
- **Nothing computes it today.** `FolderPresetDto.params.width` (`NoMercy.Data/DTOs/Encoder/FolderPresetDto.cs:26-67`)
  is kept for wire compatibility and is always `0`; `LibrariesResponseItemDto` gives each folder's presets
  by id and name only (`:116-125`). `GET api/v1/encoder/profiles/{id}/resolved`
  (`EncoderProfilesController.cs:117-136`) resolves one profile for the dashboard, and a plugin has no route
  to it.

## The change

Add what the server already knows to `PluginLibrary`, as facts, and leave the choice of a search quality
to the plugin.

```csharp
/// <summary>The encoding presets that auto-encode this library's files, as the server would run them.</summary>
public IReadOnlyList<PluginEncodingPreset> EncodingPresets { get; init; } = [];

/// <param name="Id">The preset's id.</param>
/// <param name="Name">As the dashboard shows it.</param>
/// <param name="IsDefault">Linked to a folder of this library as its default.</param>
/// <param name="MaxHeight">
/// The tallest video this preset can produce: the highest tier of an Auto ladder, the highest rung of a
/// Manual one, or <c>Video.Height</c> without a ladder. Null when the preset keeps the source's size, or
/// makes no video.
/// </param>
public record PluginEncodingPreset(string Id, string Name, bool IsDefault, int? MaxHeight);
```

- **A member, not a positional parameter**, the way `PluginLibraryEpisode.Id` was added
  (`IPluginLibraryQuery.cs:102-121`), so every plugin compiled against the older shape still constructs
  and still reads the three it knows.
- **Which presets:** those linked through `EncodingPresetFolder` to any folder of the library, narrowed to
  `Library.EncodePresetId` when that is set — exactly the set `VideoEncodeJob` would run. Once each, however
  many folders link it; `IsDefault` true when any link says so.
- **`MaxHeight`** from the preset resolved through `PresetResolver.Resolve`, so an inherited ladder counts
  and a broken preset does not throw into a plugin: a preset that does not resolve is left out.
- Filled in both projections, `PluginLibraryQuery.GetLibrariesAsync` and
  `PluginLibraryWriter.GetWritableLibrariesAsync`, with one helper so the two cannot disagree. The null
  fallback (`NullPluginLibraryAccess.cs:26`) answers the empty list.
- **Do not** add a single "resolution of the library" field that picks one of several presets on the
  server's own judgement. Which one a search should follow is the plugin's rule to keep, and a field that
  hides the choice cannot be read back when it is wrong.
- **Do not** change `VideoEncodeJob`, the resolver, the ladder generator or the preset linking.

### Verification

- A library whose folder links the built-in `H.264 Streaming (Universal)` answers one preset with
  `MaxHeight = 2160` and `IsDefault = true`.
- A folder linking a `Manual` ladder with rungs 720 and 1080 answers `MaxHeight = 1080`; a single-output
  preset of `Height = 1080` answers 1080; `Height = null` answers null.
- Two folders linking the same preset give it once; `Library.EncodePresetId` set to one of two linked
  presets gives only that one.
- A plugin built against the previous `NoMercy.Plugins.Abstractions` still loads and still reads
  `Id`, `Title` and `Type`.

It ships with the next server release: `create-release.yml:110-116` packs `NoMercy.Plugins.Abstractions`
at the server's version.

## What the plugin does meanwhile

The owner sets a library's quality on the library's form in the plugin, and every show that follows its
library is searched at it (`docs/specs/show-list.md`). Once the contract carries the presets, the plugin
reads them and draws the library's quality as a fixed value, and the owner's own value is only for a
library the server says nothing about.

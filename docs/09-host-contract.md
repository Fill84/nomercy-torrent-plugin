# The host contract

What the plugin is allowed to know about the server, beyond the library (`docs/02-library.md`) and
its own identity (`docs/01-plugin.md`).

**The media-server repository is off limits.** It may be read to confirm a signature. It is never
edited. If something the plugin needs is not in the contract, note it under **Blocked** in
`PROGRESS.md` and ask — do not work around it and do not change the server.

The full exported surface is in `docs/reference/plugin-abi-0.1.478.txt`.

## The file listing is a task

`IFileListService.GetFilesInDirectory` answers `Task<List<FileItem>>`. Walking the returned object
as a list walks the `Task` and finds nothing, which reads exactly like a server that knows of no
such file — so every staged episode was answered with "the server matched no media to this file"
and no encode was ever dispatched.

`FileItem.Match` is a `MovieOrEpisode` whose `Id` is a `dynamic` starting as an empty string:
identification is enrichment, and every video file is listed whether or not it could be resolved. An
empty id is therefore **no match**, and a job carrying one is dropped by the encoder in silence
while the queue counter moves.

## Ids are Ulids, and this plugin carries them as text

`VideoEncodeJob.LibraryId`, `FolderId` and `PresetId` are `Ulid`, and
`ILibraryRepository.GetLibraryByIdAsync` takes one. The plugin contract spells every id as a string,
so the dispatch converts through the target type's own `Parse` — never through a `Ulid` this plugin
names, because that would be a different type to the runtime however identically it is spelled.

`GetLibraryByIdAsync` also has **two overloads**: one taking an id, one taking an id and six more
arguments. `GetMethod(name)` throws `AmbiguousMatchException` on that, so the method is chosen by
how many arguments it takes.

Both were found on 23 August 2026, when the plugin had never dispatched an encode: every attempt
ended in "No encode was dispatched: Ambiguous match found". The test fakes had one overload and
string ids, so every test agreed with the plugin instead of with the server.

## `IPluginContext`

```
Ulid                  PluginId
string                DataFolderPath
ILogger               Logger
IServiceProvider      Services
HttpClient            HttpClient
IPluginConfiguration  Configuration
IPluginSecretStore    Secrets
IPluginLibraryQuery   Library
IPluginLibraryWriter  LibraryWriter        (not used — the plugin never writes to the library)
IPluginSystem         System               (no server-side implementation on dev — not a route)
IPluginPlayer         Player               (not used)
IPluginGrants         Grants
IPluginHubContext     Hub
IEventBus             EventBus
Task PublishAsync<T>(string, T, CancellationToken)
```

## Live updates

```
IPluginHubContext
    Task PushAsync(string method, object payload)
    Task PushToUserAsync(string userId, string method, object payload)
```

Pushing must never throw into a cadence: wrap it, log at debug, carry on.

## Grants

```
IPluginGrants
    Task<bool> HasAsync(string kind, string value, CancellationToken)
    Task<IReadOnlyList<string>> GetAsync(string kind, CancellationToken)
    Task RequestAsync(string kind, string value, string reason, CancellationToken)

PluginGrantKind.NetworkHost, PluginGrant.Everything
```

- A host the server has not permitted refuses **before a request is made**, with
  `Plugin network access to host 'x' is not permitted by its capabilities`. It reads exactly like the
  site refusing us.
- Hosts that ship with the plugin are declared in `plugin.json`. The runtime request is for hosts the
  owner configured — their own indexers and private trackers.
- On the measured server, approved grants did **not** survive a restart, and approving them did not
  take effect in the running process. Expect to be asked again after every deploy. That is the media
  server's business.
- **A permission refusal is never treated as the site failing**: no failure count, no backoff, no
  circuit breaker. It clears the moment the host is approved.
- Never route around the gate with the browser.

## Secrets

```
IPluginSecretStore
```

Private tracker passkeys and owner indexer API keys are stored here and never rendered, logged, put
in an error message, or written to the activity journal. A page shows that a secret is set, not its
value.

## Dispatching an encode

The plugin does not import into the library. It stages the finished video and dispatches the same
job the dashboard's *Add content* button dispatches. `FileRescanJob` only re-walks existing library
folders and cannot see a file staged elsewhere.

**The plugin asks through `Core/Ports/IEncodeGateway`, and everything below is one implementation of
it.** The cadence hands over a staged file, the episode it is, the show it belongs to and where that
show's episodes already are, and learns only whether the ask was taken. It names no type from this
page. When #30 and #35 land, the contract implementation is a second class beside this one and a
single line where the plugin is composed — `EncodeDispatch` is then deleted whole, and it is the only
reflection in the plugin, so when it goes there is none.

An implementation that refuses must say why in the log and the journal before it returns. The caller
learns nothing but "not taken" and acts the same way whatever the reason — leave the file staged,
ask again next tick — so a silent refusal is an episode that never arrives with nothing anywhere
saying why. Three of the owner's ended up in a folder nobody was watching exactly that way.

Reached by name through `IServiceProvider`, never by reference — referencing the encoder and the EF
model would make them part of this plugin's ABI, which is what the plugin contract exists to avoid.

```
IJobDispatcher      "NoMercy.MediaProcessing.Jobs.IJobDispatcher"
VideoEncodeJob      "NoMercy.MediaProcessing.Jobs.MediaJobs.VideoEncodeJob"
ILibraryRepository  "NoMercy.Data.Repositories.ILibraryRepository"
IFileListService    "NoMercy.MediaProcessing.Files.IFileListService"
```

Traps, every one measured:

- **`ILibraryRepository` is ambiguous.** Two interfaces share the name.
  `NoMercy.MediaProcessing.Libraries.ILibraryRepository` is **not registered** — the server registers
  the concrete `MediaProcessingLibraryRepository`. Asking for it resolves to nothing and every encode
  is refused with "library X was not found" about a library that is right there. A short-name
  fallback cannot save this: two types share the name.
- **Use `GetLibraryByIdAsync`, not the Lite variant.** Lite includes nothing, so the library comes
  back folderless and the encode is refused for having nowhere to go — on a library with two folders.
  The full one includes `FolderLibraries`.
- **Resolve inside a scope.** The repository is scoped because it opens a database context. A scoped
  service asked of the root provider is an exception or a null. Cadences are not requests and have no
  scope; make one.
- **`Id` must be the server's own media id, as a string.** `VideoEncodeJob` looks media up by
  `Id.ToInt()` and returns without a word when nothing matches. It was the int `0` once, which threw
  out of the reflection call, and an empty string once, which made the job find no episode and return
  in silence — the queue counter moved and the encode never ran.
- **Get that id from the server, not from the filename.**
  `IFileListService.GetFilesInDirectory(folder, libraryType)` — the two-argument overload; the
  three-argument one takes a storage driver, which is for a folder on a remote share. Walk the
  results, match on full path, read `item.Match.Id`. If nothing matched, **do not dispatch**: a job
  with no id is silently dropped, which reports success and leaves the episode in a folder nobody is
  watching.
- **The library is the show's own**, from `PluginLibraryShow.LibraryId`. That is what puts an anime
  episode in the anime library and a television episode in the tv library — the media type was
  decided by the server when the show was filed, and this plugin follows it rather than choosing.
- **Folder** is the library's *first* `FolderLibraries` entry, with no preference between them.
  Preferring one whose `Folder.Path` is non-empty is wrong: a real library's second folder is a `Z:`
  drive whose location lives on its storage driver, and the dialog lists it happily.

Fields set on the job, exactly what `ServerController.AddFiles` sets:

```
LibraryId       = the show's own LibraryId
FolderId        = first FolderLibraries entry's FolderId
Id              = the match's Id, as a string
InputFile       = Path.GetFullPath(stagedFile)
SourceDriverId  = left unset                    (a finished download is on this machine)
PresetId        = library.EncodePresetId        (null keeps the folder's own presets)
```

Then `IJobDispatcher.Dispatch(job, job.QueueName, job.Priority)` — the three-argument overload.

Nothing in this path throws. An encode that cannot be queued is one download left staged and a line
in the log naming exactly what could not be found. It used to throw out of a reflection call and
unwind the whole transfers cadence, so one type mismatch stopped every download in flight from being
looked at.

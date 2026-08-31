# The host contract

What the plugin is allowed to know about the server, beyond the library (`docs/02-library.md`) and
its own identity (`docs/01-plugin.md`).

**The media-server repository is off limits.** It may be read to confirm a signature. It is never
edited. If something the plugin needs is not in the contract, note it under **Blocked** in
`PROGRESS.md` and ask — do not work around it and do not change the server.

The full exported surface is in `docs/reference/plugin-abi-0.1.479.txt`.

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

**The plugin asks through `Core/Ports/IEncodeGateway`, and there are two implementations of it.**
The cadence hands over a staged file, the episode it is, the show it belongs to and where that
show's episodes already are, and learns whether the ask was taken and which job was queued. It names
no type from this page.

**`ContractEncodeGateway` is the one to read.** It calls `IPluginEncoder.EncodeAsync` with the
staged file, the show's library and the server's own id for the episode — `PluginLibraryEpisode.Id`
— and asks for no folder at all: a server holding the episode row knows where that show's files are
better than this plugin does. It reflects nothing and names no server type that is not in
`NoMercy.Plugins.Abstractions`. media-server #30 and #35, both closed on 30 August 2026, are what
made it possible; contract `0.1.479` is the first release carrying them.

An episode the server named no id for is refused rather than asked for with none. A null id tells
the server to identify the file again from its name, which is a text search on whatever a parser
reads out of it: the encode registers against no row, the queue counter moves, the library stays
empty, and from outside it looks like an encode still running.

`EncodeGateway.For` composes it, and there is nothing else for it to compose: the reflecting
implementation is gone.

### What became of the job

`IPluginJobs.StatusAsync` answers with `Queued`, `Running`, `Finished`, `Failed` or `Unknown`, and
with the server's own words when it failed. The plugin keeps the job id on the grab — a restart used
to lose which encode a grab was waiting on, and eleven of the owner's waited on jobs the encoder had
already thrown away while the queue sat empty.

Without it the plugin can see one thing: whether the library has the episode yet. A dead encode and
a slow one look the same from there, and both are waited out for six hours before the grab fails and
the episode goes back to missing — the same gigabytes downloaded again for a job that was never
going to finish. That six hours is still the backstop for a server that will not say.

media-server #31, closed on 30 August 2026.

An implementation that refuses must say why in the log and the journal before it returns. The caller
learns nothing but "not taken" and acts the same way whatever the reason — leave the file staged,
ask again next tick — so a silent refusal is an episode that never arrives with nothing anywhere
saying why. Three of the owner's ended up in a folder nobody was watching exactly that way.

### What it replaced

`EncodeDispatch` did all of the above by reflection, because there was no other way to ask:
`IJobDispatcher` to queue with, `VideoEncodeJob` to queue, `MediaContext` and `IFileListService` to
find the episode row. It was 588 lines, and it broke four times on server changes it could not see
coming — an ambiguous `ILibraryRepository`, a Lite query that came back folderless, a scoped service
asked of the root provider, and a media id sent as an empty string that made the job find no episode
and return without a word.

It is deleted. Those four are why media-server #30 and #35 were opened, and the file went the day
they closed. **There is no reflection anywhere in this plugin**, and no server type is named that
does not come from `NoMercy.Plugins.Abstractions`.

A server that does not offer `IPluginEncoder` is told so — once, in the log and the journal — rather
than guessed at. It needs plugin contract `0.1.479` or newer.
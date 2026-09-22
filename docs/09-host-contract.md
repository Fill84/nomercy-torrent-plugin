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

Contract 12 (`NoMercy.PluginSdk.Abstractions`, 22 September 2026). What the plugin reads:

```
Ulid                  PluginId
string                DataFolderPath
ILogger               Logger
HttpClient            HttpClient
IPluginConfiguration  Configuration
IPluginSecretStore    Secrets
IPluginLibraryQuery   Library
IPluginLibraryWriter  LibraryWriter        (not used — the plugin never writes to the library)
IPluginEncoder?       Encoder              (null unless the manifest names the encoder hook)
IPluginJobs?          Jobs                 (handed over with the encoder; what became of a job)
IPluginServerInfo     Server               (GrantedPaths, for the folder refusal; throws where a host wired none)
IPluginGrants         Grants
IPluginHubContext     Hub
IPluginEvents         Events               (plugin-to-plugin messages only; not used)
Task PublishAsync<T>(string, T, CancellationToken)
```

**What contract 12 took away, and what took its place.** `Services` (the server's container) and
`EventBus` are gone from the context. Everything the plugin reached through them has a facade or has
gone:

| Reached through `Services` / `EventBus` until 11 | Now |
| --- | --- |
| `IPluginEncoder` from the container | `Context.Encoder`, handed over only when `plugin.json` names the `encoder` hook |
| `EncodingStarted/Completed/FailedEvent` on the bus | `Context.Jobs.StatusAsync(jobId)` on every transfers pass, by the id `EncodeAsync` handed back, kept with the grab (`encode_jobs`) |
| `LibraryScanCompletedEvent` on the bus | Nothing. `Library.Watch` exists but is refused by name on an in-process plugin; the run interval covers a scan (`docs/specs/run.md`) |
| `PluginLoadedEvent` on the bus | Nothing. The plugin starts itself off the `Initialize` thread and again when the folders are saved |
| `PluginApplicationPartRegistrar` from the container, to re-attach the buttons after an update | The same registrar, from the request's own container, by the stale controller that meets the update (`LivePlugin`) |
| `IInboxMetadataProbe` and `ShowImportJob` from the container, to add a show the owner does not have | Nothing. A pack for such a show is left where it is and the History names the show to add |
| `IPluginStorage.LocationsAsync` from the container, for the folder refusal | `Context.Server.GrantedPaths` |

**The ABI is checked at load.** `plugin.json` carries `targetAbi`, `PluginAbi.IsCompatible` refuses a major
under the server's oldest (12 refuses everything under 12), and a plugin that fails is marked malfunctioned
with the reason in the server log: `ABI '10.0' is incompatible with server ABI 12.0.`

**The encoder hook widens the manifest.** A plugin whose manifest names a hook the owner's recorded consent
did not is loaded disabled, pending re-consent on the plugin's page (`PluginConsentService`,
`PluginCapabilityGuard.HasWidened`). Updating from a manifest without the hook to one with it is exactly
that, once.

**In-process is the default.** `PluginRuntimeMode.Load()` answers `InProcess` unless `runtime-mode.json`
in the plugin config folder says otherwise. The plugin's own sockets, files and processes work as they
did; out of process they would be refused, and moving them behind `Context.Net`, `Context.Storage` and
`Context.Process` is work not yet done.

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

**The plugin asks through `Core/Ports/IEncodeGateway`, and it has one method.** The cadence hands
over a staged file, the episode it is and the show it belongs to, and learns whether the ask was
taken and which job was queued. It names no type from this page.

**`ContractEncodeGateway` is the one to read.** It calls `IPluginEncoder.EncodeAsync` with the
staged file, the show's library and the server's own id for the episode — `PluginLibraryEpisode.Id`
— and asks for no folder at all: a server holding the episode row knows where that show's files are
better than this plugin does. It reflects nothing and names no server type that is not in
`NoMercy.Plugins.Abstractions`. media-server #30 and #35, both closed on 30 August 2026, are what
made it possible; contract `0.1.479` is the first release carrying them.

**An episode the plugin cannot name an id for is not asked for at all.** Read the server's own
source rather than the doc comment on `mediaId`: `PluginEncoder` puts the id verbatim into
`VideoEncodeJob.Id`, and `VideoEncodeJob.GetFileMetaData` resolves it against `Movies.Id` or
`Episodes.Id` and nothing else — both keyed by the provider's own id, `DatabaseGeneratedOption.None`.
No id resolves no row, so `Success` is false and every caller returns having done no work, while the
queue records the job as finished. That is what the owner watched on 31 August 2026: nine files, nine
jobs finished inside two minutes, an empty library. A show id would not help either; there is nowhere
on that path that a `Tv` row is created.

So the gateway has no second method and the plugin has no way to hand a file over unnamed.
`EncodeGateway.For` composes the one implementation, and there is nothing else for it to compose:
the reflecting implementation is gone.

**A show that is in no library is added first, with the server's own import job.** That is what makes
an id exist to dispatch by. `Hosting/ShowImport.cs` asks `IInboxMetadataProbe.SearchTvAsync` which
show the files name and then dispatches `DispatchJob<ShowImportJob>(id, libraryId)` — the same call
the dashboard's *Add content* makes, and the only thing anywhere that puts a show in a library. The
tick after the import lands sees an ordinary grab.

It is the only reflection left in the plugin, and it is there because the contract offers no way to
ask a provider anything or to queue one of the server's own jobs. Asked once per run per show: the
import sits on the server's queue and a tick a minute later still finds the show missing.

### What became of the job

Asked, on contract 12: `IPluginJobs.StatusAsync(jobId)`, with the id `EncodeAsync` handed back. That id
is `QueuePayloadHash.For(payload)` — a hash of the job, chosen because a queue row id is not stable — and
the server's own `PluginJobs` looks it up in the queue's tables by exactly that hash. The plugin keeps it
with the grab, per episode (`grabs.encode_jobs`, migration 017), so a restart with a dispatched grab still
knows what to ask about, and asks on every transfers pass (`EncoderSays`).

**What the answer means.** In the queue and not reserved: Queued. Reserved: Running. In the failed table:
Failed, with the exception the server wrote there. In neither: Finished — a plugin only holds an id the
server handed it, so a row that is gone is a job that ran. Unknown, or a facade that refuses, is answered
as nothing, and nothing is never "finished".

**And that is a better answer than the events were.** Until contract 12 the plugin heard
`EncodingStartedEvent`, `EncodingCompletedEvent` and `EncodingFailedEvent` on the server's bus, matched
on the media row they carry. A failed event was not the end of the job: `JobQueue.FailJob` puts the job
back with a back-off while `Attempts < maxAttempts` (three), and only the last attempt moves it to
`FailedJobs`, which said nothing on the bus — so the plugin could never close on one. The failed table
is what the facade reads, so a job is failed here only once the server has truly given it up. A plugin on
contract 12 has no bus in any case: `IPluginEvents` relays plugin-to-plugin messages and nothing the
server publishes.

**Two endings still say nothing to the encoder's own record.** A job taken out of the queue by hand,
and a job that ends with nothing to encode: the library folder not found, no preset, or every preset
already encoded. Both read as Finished here, which is right — the library is the proof.

So the library decides. A grab waiting on an encode is closed by the pass that finds its episode in the
library; a pass runs on start, when the folders are saved, when a download finishes, and on the transfers
cadence — and the encoder's word is what keeps a staged file the server is still reading from the sweep.
It is never given up on by a clock and never asked for a second time — the owner's ruling of
14 September 2026, replacing a six-hour give-up that put the episode back to missing and downloaded it
again, and a re-dispatch after every restart that put a second job in a queue that had kept the first.

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
they closed. **Nothing on the way to an encode reflects any more**, and no server type is named on
that path that does not come from `NoMercy.Plugins.Abstractions`.

**One file still reflects, and it is `Hosting/ShowImport.cs`.** It asks
`IInboxMetadataProbe.SearchTvAsync` which show a torrent names and dispatches the server's own
`ShowImportJob` for it, because the contract offers no way to ask a provider anything or to queue one
of the server's jobs. Every step is guarded: a server that renames one of those three types says so,
once, and a pack for a show in no library is left where it is with its show named on the History
page.

A server that does not offer `IPluginEncoder` is told so — once, in the log and the journal — rather
than guessed at. It needs plugin contract `0.1.479` or newer.
# Finishing the Download Loop Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Take the plugin from "chooses a release and then silently throws it away" to "downloads the episode, imports it, and shows the owner what it is doing at every step".

**Architecture:** The chain up to the decision is proven working against a real capture. The break is at `TorrentEngine.AddAsync`, which awaits BEP 9 metadata before returning — five minutes of blocking, then a `MetadataException` that unwinds the whole search cycle before the grab is ever recorded. Adding a magnet becomes non-blocking: the info hash is known from the magnet itself, so the torrent is registered immediately and metadata is fetched on a background task. Everything downstream already polls `TransfersAsync`, so it needs a new state to poll rather than a new mechanism.

**Tech Stack:** .NET 10, C# 13, xUnit + FluentAssertions. Build and test with `~/.dotnet/dotnet.exe` (see Global Constraints).

## Global Constraints

- **SDK:** `"$USERPROFILE/.dotnet/dotnet.exe"`. A bare `dotnet` on this machine is 8.0 and cannot build this repo.
- **Build/test:** `"$USERPROFILE/.dotnet/dotnet.exe" test nomercy-torrent-plugin.sln -c Release --nologo`. Every task ends green. Baseline at plan time: **1029 passing**.
- **`TreatWarningsAsErrors` is on.** A warning fails the build.
- **No hardcoded content.** No show titles, no episode names, no library assumptions. Anything site- or network-specific is a setting with a default, never a constant buried in logic.
- **Comments explain why, not what.** Match the density and voice of the surrounding code; this codebase documents the failure a line prevents.
- **Never break a self-hosted user.** `EngineState` gains a member; every `switch` over it must keep compiling and keep behaving.
- **Commit after every task**, conventional commits, no attribution footer.
- **Deploy needs the server stopped** — that is the owner's call, and whoever stops it finishes the restart.

---

## File Structure

| File | Responsibility |
| --- | --- |
| `Core/Engine/ITorrentEngine.cs` | `EngineState.Resolving`; `EngineTransfer` unchanged otherwise |
| `Core/Engine/TorrentEngine.cs` | Non-blocking magnet add, background resolution, resolving/failed bookkeeping |
| `Core/Engine/ResolvingTorrent.cs` | **New.** One magnet whose metadata has not arrived yet |
| `Core/Orchestration/DownloadOrchestrator.cs` | Per-episode failure isolation; history for a failure; resolving-aware transfer cycle |
| `Core/Indexers/SiteListingParser.cs` | Built magnets carry trackers |
| `Core/Indexers/SiteIndexer.cs` | Passes the configured trackers to the parser |
| `Configuration/TorrentDownloaderSettings.cs` | `DefaultTrackers` |
| `Views/DownloadsView.cs` | A torrent that is still finding peers |
| `Views/HistoryView.cs` | Nothing — it already renders whatever history holds |

---

### Task 1: A magnet's info hash is known before any peer is asked

**Files:**
- Modify: `src/NoMercy.Plugin.TorrentDownloader.Core/Engine/ITorrentEngine.cs:1-25`
- Modify: `src/NoMercy.Plugin.TorrentDownloader.Core/Engine/TorrentEngine.cs:59-86`
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Engine/ResolvingTorrent.cs`
- Test: `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Engine/TorrentEngineResolvingTests.cs`

**Interfaces:**
- Consumes: `MagnetLink.Parse(string)` → `MagnetLink` with `InfoHash` (byte[]), `Trackers` (IReadOnlyList&lt;string&gt;), `DisplayName` (string?).
- Produces: `EngineState.Resolving`; `TorrentEngine.AddAsync` returning within milliseconds for a magnet; `ResolvingTorrent` record with `InfoHash`, `Request`, `StartedAt`, `FailureReason`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task AddAsync_AMagnetReturnsBeforeAnyPeerHasAnswered()
{
    // A dialer that never answers is the swarm this actually failed against.
    TorrentEngine engine = Engine(new NeverAnsweringDialer());

    Task<string> add = engine.AddAsync(new TorrentRequest
    {
        Source = $"magnet:?xt=urn:btih:{Hash}",
        DestinationFolder = Folder,
    }, CancellationToken.None);

    string infoHash = await add.WaitAsync(TimeSpan.FromSeconds(2));

    infoHash.Should().Be(Hash, "the hash is in the magnet - nobody has to be asked for it");
}

[Fact]
public async Task TransfersAsync_ReportsAMagnetWaitingOnItsMetadata()
{
    TorrentEngine engine = Engine(new NeverAnsweringDialer());
    await engine.AddAsync(Magnet(), CancellationToken.None);

    EngineTransfer transfer = (await engine.TransfersAsync(CancellationToken.None))
        .Should().ContainSingle().Subject;

    transfer.State.Should().Be(EngineState.Resolving);
    transfer.InfoHash.Should().Be(Hash);
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `"$USERPROFILE/.dotnet/dotnet.exe" test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/NoMercy.Plugin.TorrentDownloader.Core.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~TorrentEngineResolving"`
Expected: FAIL — the first test times out at two seconds (today it blocks for the five-minute `MetadataTimeout`), the second on `EngineState.Resolving` not existing.

- [ ] **Step 3: Add the state**

In `ITorrentEngine.cs`, inside `enum EngineState`, above `Downloading`:

```csharp
    /// <summary>
    /// Added, and waiting for a peer to hand over what the torrent actually contains.
    ///
    /// <para>
    /// Its own state rather than Downloading-at-zero-percent, because the two fail for
    /// different reasons and only one of them is worth a progress bar. A magnet names an
    /// info hash and nothing else; until some peer answers, the engine does not know how
    /// many bytes there are to be at zero percent of.
    /// </para>
    /// </summary>
    Resolving,
```

- [ ] **Step 4: Add the resolving record**

Create `ResolvingTorrent.cs`:

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Engine;

/// <summary>
/// A magnet the engine has taken on and cannot start yet.
///
/// <para>
/// It exists so that "we are working on this" is a thing the engine can say. Before it,
/// AddAsync blocked until BEP 9 answered and threw when it did not - which unwound the
/// caller's whole cycle and left no record anywhere that the torrent had ever been chosen.
/// The owner saw nothing at all for a fortnight.
/// </para>
/// </summary>
internal sealed record ResolvingTorrent
{
    public required string InfoHash { get; init; }
    public required TorrentRequest Request { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Set when resolution gave up. The torrent stays listed so the reason can be read.</summary>
    public string? FailureReason { get; set; }
}
```

- [ ] **Step 5: Make AddAsync non-blocking for a magnet**

Replace `TorrentEngine.AddAsync` (lines 59-86) with:

```csharp
    public async Task<string> AddAsync(TorrentRequest request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A .torrent URL is a fetch and a parse - fast, and it fails in a way the caller
        // can act on. A magnet is a conversation with a swarm that may not exist, so it
        // must not be had on the caller's thread: this used to await BEP 9 for five
        // minutes and then throw, which took the whole search cycle with it.
        if (!request.Source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            return await StartAsync(await ResolveAsync(request, ct), request, ct);

        MagnetLink magnet = MagnetLink.Parse(request.Source);
        string infoHash = Convert.ToHexStringLower(magnet.InfoHash);

        if (_torrents.ContainsKey(infoHash) || _resolving.ContainsKey(infoHash))
            return infoHash;

        _resolving[infoHash] = new ResolvingTorrent
        {
            InfoHash = infoHash,
            Request = request,
            StartedAt = now(),
        };

        ResolveInBackground(infoHash, request);

        return infoHash;
    }

    /// <summary>
    /// Asks the swarm for the metadata and starts the torrent when it arrives.
    ///
    /// <para>
    /// Fire and forget on purpose, and the only place in the engine that is. Nothing awaits
    /// it because the point is that the caller does not: the transfer list is how anyone
    /// learns how it went, which is the same way they learn about every other change of
    /// state here.
    /// </para>
    /// </summary>
    private void ResolveInBackground(string infoHash, TorrentRequest request)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                TorrentMetadata metadata = await ResolveAsync(request, _lifetime.Token);

                await StartAsync(metadata, request, _lifetime.Token);

                _resolving.TryRemove(infoHash, out _);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // The engine is going away. Not a failure of this torrent.
                _resolving.TryRemove(infoHash, out _);
            }
            catch (Exception failure)
            {
                // Recorded on the torrent rather than thrown into nothing. A background
                // task's exception has nowhere to go, and the whole reason this moved off
                // the caller's thread was that throwing there destroyed the cycle.
                if (_resolving.TryGetValue(infoHash, out ResolvingTorrent? waiting))
                    waiting.FailureReason = failure is MetadataException
                        ? "no peer offered this torrent's contents - the swarm may be dead"
                        : failure.Message;
            }
        });
    }

    /// <summary>Everything after the metadata is known, whichever way it was learned.</summary>
    private async Task<string> StartAsync(TorrentMetadata metadata, TorrentRequest request, CancellationToken ct)
    {
        string infoHash = Convert.ToHexStringLower(metadata.InfoHash);

        // Adding the same torrent twice is not an error - two episodes can want one
        // season pack, and a retry can arrive while the first attempt is still running.
        if (_torrents.ContainsKey(infoHash))
            return infoHash;

        Directory.CreateDirectory(options.StateFolder);

        FilePieceStore store = new(metadata, options.DownloadFolder);
        FileResumeStore resume = new(options.StateFolder);

        Bitfield have = await resume.LoadAsync(metadata, ct) ?? new Bitfield(metadata.PieceCount);

        TorrentSession session = new(metadata, store, resume, have, options.Policy);

        RunningTorrent running = new(infoHash, metadata, session, store, request, now());
        _torrents[infoHash] = running;

        running.Start(this, ct);

        return infoHash;
    }
```

Add beside `_torrents` (line ~52):

```csharp
    private readonly ConcurrentDictionary<string, ResolvingTorrent> _resolving = new();

    // Cancelled by Dispose, so a background resolution does not outlive the engine and
    // keep the plugin's load context alive - which on Windows locks the plugin's files.
    private readonly CancellationTokenSource _lifetime = new();
```

- [ ] **Step 6: Report resolving torrents in the transfer list**

In `TransfersAsync`, before returning the running torrents' transfers, prepend:

```csharp
        foreach (ResolvingTorrent waiting in _resolving.Values)
        {
            transfers.Add(new EngineTransfer
            {
                InfoHash = waiting.InfoHash,
                State = waiting.FailureReason is null ? EngineState.Resolving : EngineState.Failed,
                FailureReason = waiting.FailureReason,
            });
        }
```

- [ ] **Step 7: Cancel background work on dispose**

In `DisposeAsync`, before disposing the torrents:

```csharp
        await _lifetime.CancelAsync();
        _lifetime.Dispose();
        _resolving.Clear();
```

- [ ] **Step 8: Run the tests**

Run: `"$USERPROFILE/.dotnet/dotnet.exe" test nomercy-torrent-plugin.sln -c Release --nologo`
Expected: PASS, 1031+.

- [ ] **Step 9: Commit**

```bash
git add -A && git commit -m "fix(engine): adding a magnet no longer waits five minutes for a stranger

AddAsync awaited BEP 9 before it returned anything. On a swarm with no peers
that is five minutes of a blocked search cycle followed by a MetadataException
thrown through the caller - so the grab was never recorded, the rest of the
batch was never searched, and the owner saw no trace of a download the plugin
had already decided to make.

The info hash is in the magnet. The torrent is registered against it at once and
the swarm is asked on a background task; the transfer list reports Resolving
until an answer arrives and Failed with a reason when none does."
```

---

### Task 2: One episode's failure costs one episode

**Files:**
- Modify: `src/NoMercy.Plugin.TorrentDownloader.Core/Orchestration/DownloadOrchestrator.cs` — the `foreach` in `SearchCycleAsync` and the `engine.AddAsync` call in `GrabAsync`
- Test: `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Orchestration/DownloadOrchestratorTests.cs`

**Interfaces:**
- Consumes: `HistoryEvent.Failed`, `IDownloadStore.RecordHistoryAsync`.
- Produces: no signature change. `SearchCycleAsync` still returns `SearchCycle(Searched, Grabbed)`.

- [ ] **Step 1: Write the failing test**

```csharp
// A cadence that stops at the first bad episode never reaches the good ones, and on a
// real server the first one was bad every single cycle.
[Fact]
public async Task SearchCycleAsync_AnEpisodeThatThrowsDoesNotStopTheOnesBehindIt()
{
    _library.Add(showId: 1, "First", "/media/first", [(1, 1, true), (1, 2, false)],
        status: ShowStatus.Returning);
    _library.Add(showId: 2, "Second", "/media/second", [(1, 1, true), (1, 2, false)],
        status: ShowStatus.Returning);
    _library.SetAirDate(1, 1, 2, Now.AddDays(-2));
    _library.SetAirDate(2, 1, 2, Now.AddDays(-2));

    await Orchestrator().RefreshWantedAsync(CancellationToken.None);

    _search.Results = [Release()];
    _engine.ThrowOnceWith = new InvalidOperationException("the swarm hung up");

    SearchCycle cycle = await Orchestrator().SearchCycleAsync(CancellationToken.None);

    cycle.Searched.Should().Be(2, "the second episode is still owed a search");
    cycle.Grabbed.Should().Be(1);
}

[Fact]
public async Task SearchCycleAsync_SaysOnThePageWhyAnEpisodeCouldNotBeStarted()
{
    await WantOneEpisodeAsync();
    _search.Results = [Release()];
    _engine.ThrowOnceWith = new InvalidOperationException("the swarm hung up");

    await Orchestrator().SearchCycleAsync(CancellationToken.None);

    HistoryEntry entry = _store.History.Should().ContainSingle().Subject;
    entry.Event.Should().Be(HistoryEvent.Failed);
    entry.Detail.Should().Contain("the swarm hung up");
}
```

Add to `FakeEngine` in the same file:

```csharp
        /// <summary>Throws on the next Add and then behaves. The failure this stands in for is a swarm that will not answer.</summary>
        public Exception? ThrowOnceWith { get; set; }
```

and at the top of its `AddAsync`:

```csharp
            if (ThrowOnceWith is Exception failure)
            {
                ThrowOnceWith = null;
                throw failure;
            }
```

- [ ] **Step 2: Run it and watch it fail**

Run: `… --filter "FullyQualifiedName~DoesNotStopTheOnesBehindIt|FullyQualifiedName~WhyAnEpisodeCouldNotBeStarted"`
Expected: FAIL — the exception escapes `SearchCycleAsync`.

- [ ] **Step 3: Isolate the failure**

In `SearchCycleAsync`, replace the body of the loop's grab with:

```csharp
            searched++;

            IReadOnlyList<EpisodeKey>? covered;

            try
            {
                covered = await TryGrabAsync(episode, ct);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                // One episode, one failure. This used to unwind the whole cycle: the
                // episodes behind it were never searched, and because the throw came
                // before AddGrabAsync there was no record anywhere that anything had been
                // tried. Recorded here so the page can say what happened, and on we go.
                await store.RecordHistoryAsync(new HistoryEntry
                {
                    At = now(),
                    Event = HistoryEvent.Failed,
                    Key = episode.Key,
                    ShowTitle = episode.ShowTitle,
                    ReleaseTitle = episode.EpisodeTitle ?? Format(episode.Key),
                    Detail = failure.Message,
                }, ct);

                continue;
            }

            if (covered is null)
                continue;

            settled.UnionWith(covered);
            grabbed++;
```

Add the private helper beside `NotOutYet`:

```csharp
    /// <summary>An episode named the way history reads best when the release has no name yet.</summary>
    private static string Format(EpisodeKey key) => $"S{key.Season:D2}E{key.Episode:D2}";
```

- [ ] **Step 4: Run the tests**

Run: `"$USERPROFILE/.dotnet/dotnet.exe" test nomercy-torrent-plugin.sln -c Release --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "fix(core): one episode's failure costs one episode

A throw out of the engine unwound the whole search cycle, so the episodes behind
the bad one were never searched - and because it happened before AddGrabAsync,
nothing anywhere recorded that a release had been chosen and lost. Caught per
episode, written to history with the reason, and the cycle carries on."
```

---

### Task 3: A built magnet carries trackers

**Files:**
- Modify: `src/NoMercy.Plugin.TorrentDownloader.Core/Indexers/SiteListingParser.cs` — `Parse` gains a tracker parameter
- Modify: `src/NoMercy.Plugin.TorrentDownloader.Core/Indexers/SiteIndexer.cs:108-135`
- Modify: `src/NoMercy.Plugin.TorrentDownloader/Configuration/TorrentDownloaderSettings.cs`
- Modify: `src/NoMercy.Plugin.TorrentDownloader/Hosting/DownloadPipeline.cs:255-261`
- Modify: `src/NoMercy.Plugin.TorrentDownloader/Views/SettingsView.cs`
- Test: `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Indexers/SiteListingParserRealSiteTests.cs`

**Interfaces:**
- Consumes: `TorrentDownloaderSettings.DefaultTrackers` (`List<string>`).
- Produces: `SiteListingParser.Parse(string html, IReadOnlyList<string> trackers)`. The existing single-argument call sites are updated; no overload is kept, so a caller that forgets trackers does not compile.

- [ ] **Step 1: Write the failing test**

```csharp
/// <summary>
/// A magnet built from a hash names a torrent and no way to find anyone who has it. DHT
/// alone answered nobody on a real swarm: five minutes of asking, then a MetadataException.
/// The trackers are the owner's setting, so a site that offers none still resolves.
/// </summary>
[Fact]
public void Parse_PutsTheConfiguredTrackersOnAMagnetItBuilt()
{
    IReadOnlyList<SiteRow> rows = SiteListingParser.Parse(
        Fixtures.Text("limetorrents-search.html"),
        ["udp://tracker.example:1337/announce", "udp://other.example:80/announce"]);

    SiteRow row = rows.First();

    row.MagnetUri.Should().Contain("tr=udp%3A%2F%2Ftracker.example%3A1337%2Fannounce");
    row.MagnetUri.Should().Contain("tr=udp%3A%2F%2Fother.example%3A80%2Fannounce");
}

/// <summary>A site that publishes its own magnet already names its swarm; nothing is added to it.</summary>
[Fact]
public void Parse_LeavesAMagnetTheSitePublishedExactlyAsItFoundIt()
{
    string html = """<a href="magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=A.Show.S01E01">x</a>""";

    SiteListingParser.Parse(html, ["udp://tracker.example:1337/announce"])
        .Should().ContainSingle().Which.MagnetUri.Should().NotContain("tracker.example");
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `… --filter "FullyQualifiedName~PutsTheConfiguredTrackers"`
Expected: FAIL — `Parse` takes one argument.

- [ ] **Step 3: Thread the trackers through the parser**

Change the signature and the built-magnet branch:

```csharp
    public static IReadOnlyList<SiteRow> Parse(string html, IReadOnlyList<string> trackers)
```

and where the magnet is built:

```csharp
            // Trackers on a built magnet, never on one the site published: that one already
            // names the swarm its own users are in, and appending to it is guesswork on top
            // of fact. A hash-only magnet has nothing but DHT, and DHT alone answered nobody
            // on a real swarm - five minutes of asking, then nothing.
            string announce = string.Concat(trackers.Select(tracker => $"&tr={Uri.EscapeDataString(tracker)}"));

            string magnet = $"magnet:?xt=urn:btih:{hash}&dn={Uri.EscapeDataString(title)}{announce}";
```

- [ ] **Step 4: Update the two call sites**

`SiteIndexer.SearchAsync` gains a `trackers` constructor parameter and passes it:

```csharp
public sealed class SiteIndexer(
    string name,
    int priority,
    string searchUrlTemplate,
    ChallengeAwareFetch fetch,
    IReadOnlyList<string> trackers
) : IIndexer
```

```csharp
            .. SiteListingParser.Parse(html, trackers).Select(row => new ReleaseInfo
```

Existing tests calling `SiteListingParser.Parse(html)` become `Parse(html, [])`.

- [ ] **Step 5: Add the setting**

In `TorrentDownloaderSettings`:

```csharp
    /// <summary>
    /// Trackers added to a magnet this plugin had to build itself.
    ///
    /// <para>
    /// A site that lists a torrent file rather than a magnet gives an info hash and no
    /// swarm. DHT alone is not enough - on a real server it asked for five minutes and
    /// nobody answered. These are ordinary public trackers, and they are a setting rather
    /// than a constant because which ones work changes and the owner should be able to see
    /// and change what their server talks to.
    /// </para>
    /// </summary>
    public List<string> DefaultTrackers { get; set; } =
    [
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.demonii.com:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://explodie.org:6969/announce",
    ];
```

In `DownloadPipeline.Build`, the `"site"` case passes `loaded.Settings.DefaultTrackers`.

In `SettingsView.BuildGeneralForm`, after `allowSeasonPacks`:

```csharp
            new()
            {
                Name = "defaultTrackers",
                Label = "Trackers for magnets built from a hash",
                Value = string.Join(", ", settings.DefaultTrackers),
            },
```

and in `ApplyGeneral`, beside the other optional fields:

```csharp
        // Left alone when the field is absent, like every other optional one. Emptied
        // deliberately is honoured: an owner who wants DHT only is allowed to say so.
        if (request.DefaultTrackers is not null)
        {
            merged.DefaultTrackers =
            [
                .. request.DefaultTrackers
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(tracker => Uri.TryCreate(tracker, UriKind.Absolute, out _)),
            ];
        }
```

with `public string? DefaultTrackers { get; init; }` on `SaveSettingsRequest`.

- [ ] **Step 6: Run the tests**

Run: `"$USERPROFILE/.dotnet/dotnet.exe" test nomercy-torrent-plugin.sln -c Release --nologo`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat(core): a magnet built from a hash names a swarm to ask

A site that lists a torrent file gives an info hash and nowhere to find peers,
so the magnet built from it had only DHT - which on a real swarm answered nobody
for five minutes and then gave up. The trackers are a visible setting with a
default rather than a constant: which ones work changes, and an owner should be
able to see what their server talks to."
```

---

### Task 4: The pages show a download that has not started yet

**Files:**
- Modify: `src/NoMercy.Plugin.TorrentDownloader.Core/Orchestration/DownloadOrchestrator.cs` — `TransfersCycleAsync`
- Modify: `src/NoMercy.Plugin.TorrentDownloader.Core/Store/IDownloadStore.cs` — `GrabState.Resolving`
- Modify: `src/NoMercy.Plugin.TorrentDownloader/Views/DownloadsView.cs`
- Test: `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Orchestration/DownloadOrchestratorTests.cs`, `tests/NoMercy.Plugin.TorrentDownloader.Tests/Views/DownloadsViewTests.cs`

**Interfaces:**
- Consumes: `EngineState.Resolving` from Task 1.
- Produces: `GrabState.Resolving`; `DownloadsView` renders a "Finding peers" block.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task TransfersCycleAsync_MarksAGrabThatIsStillLookingForPeers()
{
    await WantOneEpisodeAsync();
    _search.Results = [Release()];
    await Orchestrator().SearchCycleAsync(CancellationToken.None);

    _engine.Transfers = [new EngineTransfer { InfoHash = "abc123", State = EngineState.Resolving }];

    await Orchestrator().TransfersCycleAsync(CancellationToken.None);

    (await _store.FindGrabAsync("abc123", CancellationToken.None))!
        .State.Should().Be(GrabState.Resolving);
}
```

```csharp
[Fact]
public void Build_SaysWhenATorrentIsStillLookingForPeers()
{
    PluginView view = DownloadsView.Build(
        [new Transfer { InfoHash = "abc", BytesTotal = 0 }],
        [Grab("abc", "Some.Show.S01E01") with { State = GrabState.Resolving }]);

    PluginNodes.Says(view, "Finding peers").Should().BeTrue();
}
```

- [ ] **Step 2: Run and watch both fail**

Expected: FAIL — `GrabState.Resolving` does not exist.

- [ ] **Step 3: Add the state and the transition**

In `IDownloadStore.cs`, in `enum GrabState`, before `Downloading`:

```csharp
    /// <summary>Handed to the engine, which is asking the swarm what this torrent contains. No bytes yet, and none owed.</summary>
    Resolving,
```

In `TransfersCycleAsync`'s switch, above the `Downloading` case:

```csharp
                case EngineState.Resolving when grab.State == GrabState.Grabbed:
                    await store.UpdateGrabAsync(transfer.InfoHash, GrabState.Resolving, null, null, ct);
                    break;
```

- [ ] **Step 4: Draw it**

In `DownloadsView`, where a block's status line is composed, before the percentage:

```csharp
        // No bar and no percentage: there is nothing to be a percentage of until a peer
        // says how big the torrent is. Saying "0%" here reads as a stalled download rather
        // than one that has not begun, which is a different thing to be worried about.
        if (grab?.State == GrabState.Resolving)
            return "Finding peers…";
```

- [ ] **Step 5: Run the tests**

Run: `"$USERPROFILE/.dotnet/dotnet.exe" test nomercy-torrent-plugin.sln -c Release --nologo`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat(ui): a download that has not started yet says so

A magnet is registered before anyone has said what it contains, so there is a
real span with a grab, no bytes and no size. Drawn as Finding peers rather than
0%, because a download at nought per cent reads as stalled and this one has not
begun."
```

---

### Task 5: Prove the loop on the owner's server

**Files:** none — this is the verification the previous four exist for.

- [ ] **Step 1: Build and deploy**

```bash
"$USERPROFILE/.dotnet/dotnet.exe" build src/NoMercy.Plugin.TorrentDownloader/NoMercy.Plugin.TorrentDownloader.csproj -c Release --nologo
```

Ask the owner before stopping the server. Then, per `docs/deploying-to-a-server.md`, stop it, copy `NoMercy.Plugin.TorrentDownloader.dll` and `.Core.dll`, start it, and ask the owner to accept the plugin if it does not load within three minutes.

- [ ] **Step 2: Watch one search cycle**

```bash
ssh beast-unit 'grep -a "Torrent Downloader" "$LOCALAPPDATA/NoMercy/log/run-*.jsonl" | tail -20'
```

Expected: `searched for N episode(s)` and no `Failed to execute cron job`.

- [ ] **Step 3: Confirm a grab reached the store**

```bash
ssh beast-unit 'python -c "import json;d=json.load(open(r\"C:\Users\phill\AppData\Local\NoMercy\plugins\data\1SBQT26FHF98EBRPYVRGD92CZF\downloads.json\"));print(len(d[\"Grabs\"]),len(d[\"History\"]))"'
```

Expected: `Grabs` above zero, or `History` carrying a Failed entry with a readable reason. **Either is a pass for this task** — the point is that the attempt is no longer invisible.

- [ ] **Step 4: Follow one download to the library**

Watch until a transfer reaches `Completed`, then confirm the move and the encode dispatch, which have never been verified on a server (`docs/HANDOFF.md`). Check the intake folder `D:\nomercy_finished_downloads` and the server log for a `VideoEncodeJob`.

- [ ] **Step 5: Record what happened**

Update `docs/HANDOFF.md` with the first verified download end to end, or with exactly where it stopped.

---

### Task 6: The Allow button records the grant

**Files:**
- Modify: `nomercy-media-server/src/NoMercy.Api/Controllers/V1/Dashboard/Plugins/PluginController.cs:167-185`
- Test: `nomercy-media-server/tests/NoMercy.Tests.Api/…/PluginGrantsTests.cs`

Measured on the owner's server: clicking Allow cleared the three pending requests and wrote no grant, twice. The server therefore took `decision.Granted` as false. The DTO carries `[JsonProperty("granted")]` (Newtonsoft) and the API is configured `AddNewtonsoftJson`, so binding *should* work — which means the defect is either in what the client sends or in a second serializer on this path.

- [ ] **Step 1: Reproduce at the boundary** — a `NoMercyApiFactory` test that posts exactly what the browser posts (`{plugin_id, kind, value, reason, requested_at, granted: true}`) and asserts `IPluginGrantStore.Holds(...)` afterwards. Expect it to fail.
- [ ] **Step 2: Fix whichever side is wrong.** If binding is the fault, the DTO gains `[JsonPropertyName]` beside `[JsonProperty]`. If the client is, `resolveGrant` in `nomercy-app-web/src/composables/usePlugins.ts:119-122` sends `{kind, value, granted}` rather than spreading the whole request.
- [ ] **Step 3: Never silently clear.** `ResolveGrant` returning "Grant given" while writing nothing is what made this invisible; on the granted path it re-reads and returns an error if the grant is not there afterwards.
- [ ] **Step 4: Remove the manual record** on beast-unit and re-grant through the button, proving it works.
- [ ] **Step 5: Commit** in the media-server repo.

---

### Task 7: The library plugin's URL, end to end

- [ ] **Step 1:** Ship `nomercy-app-web` commit `d1ca59bf7`, which registers the `libraries` segment alongside `library`.
- [ ] **Step 2:** Once it is live, revert `nomercy-media-server` commit `b0182b20` on branch `fix/hold-library-segment` so the server advertises `/libraries` again, and rebuild.
- [ ] **Step 3:** Confirm `https://app.nomercy.tv/libraries/plugins/1SBQT26FHF98EBRPYVRGD92CZF` renders.
- [ ] **Step 4:** `Sidebar.vue:32-34` draws plugin entries for Music, Dashboard and Settings only, so a library-mounted plugin has no sidebar entry. Add the library section.

---

### Task 8: The tab bar stops moving

The current tab is a `PluginBadge` and the rest are `PluginButton`s, so the row's width changes by the badge/button difference as the owner moves along it. A badge carries no action, so all-badges breaks navigation, and `PluginButton` draws every variant but `danger` identically, so a button cannot be marked as current.

- [ ] **Step 1:** Give `PluginButton` a visually distinct variant in the design system (`nomercy-app-web`), so "current" is expressible on a button.
- [ ] **Step 2:** Change `Pages.Tabs` to render every tab as a button, the current one with that variant.
- [ ] **Step 3:** Assert in `PagesTests` that every tab is the same component type.

---

## Self-Review

**Spec coverage.** The five things asked for over this session: shows the owner actually has (done before this plan), correct episodes for running shows (done), a plugin that downloads (Tasks 1-3, proven by Task 5), an owner who can see what it is doing (Tasks 2 and 4 plus the Queue rework already shipped), and the loose ends that would otherwise rot (Tasks 6-8).

**Placeholders.** None. Every code step carries the code. Task 6's step 2 names both candidate fixes because the measurement that distinguishes them is step 1 — that is a decision the evidence makes, not a gap.

**Type consistency.** `EngineState.Resolving` (Task 1) is consumed by `TransfersCycleAsync` (Task 4). `GrabState.Resolving` (Task 4) is read by `DownloadsView` (Task 4). `SiteListingParser.Parse(html, trackers)` (Task 3) is called by `SiteIndexer` (Task 3) and by the tests updated in the same task. `ResolvingTorrent` is `internal` and never crosses the engine boundary; the transfer list is the only thing that does.

**Ordering.** Tasks 1-4 are strictly ordered — 2 is what makes 1's failures visible, and 4 renders the state 1 introduces. Tasks 6-8 are independent of them and of each other.

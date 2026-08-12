// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Engine;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Library;
using NoMercy.Plugin.TorrentDownloader.Core.Orchestration;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugin.TorrentDownloader.Core.Swarm;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Orchestration;
/// <summary>
/// Everything an orchestrator test needs and nothing it is about: the store, the fakes,
/// a fixed clock, and the small builders that put a show or a grab in place.
///
/// <para>
/// One fixture, shared by the files that each take one cadence. It used to be the top and
/// tail of a two-thousand-line file whose middle was every cadence at once, so a change to
/// the search loop and a change to the import loop touched the same file for no reason
/// beyond where the helpers happened to live.
/// </para>
/// </summary>
public abstract class DownloadOrchestratorTestBase
{
    protected static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    protected readonly InMemoryDownloadStore _store = new();
    protected readonly FakeLibrary _library = new();
    protected readonly FakeSearch _search = new();
    protected readonly FakeEngine _engine = new();
    protected readonly FakeIntake _intake = new();
    protected readonly FakeChooser _chooser = new();
    protected readonly FakeFeed _feed = new();
    protected readonly FakeResolver _resolver = new();

    protected DownloadOrchestrator Orchestrator(OrchestratorOptions? options = null) => new(
        _library,
        _store,
        _search,
        _chooser,
        _engine,
        _intake,
        options ?? new OrchestratorOptions { DownloadFolder = "/downloads" },
        new PrivateTrackerRegistry([]),
        () => Now,
        _feed,
        _ => FreeBytes,
        _resolver);

    /// <summary>What the disk claims to have left. Enough for anything, unless a test says otherwise.</summary>
    protected long? FreeBytes { get; set; } = 500L * 1024 * 1024 * 1024;

    protected static ReleaseInfo Release(string title = "Some.Show.S01E01.1080p.WEB-DL", string? hash = "abc123") => new()
    {
        IndexerName = "site-a",
        TorrentId = "1",
        Title = title,
        InfoHash = hash,
        MagnetUri = $"magnet:?xt=urn:btih:{hash ?? "0000000000000000000000000000000000000000"}",
        SizeBytes = 2_000_000_000,
        Seeders = 40,
        Trackers = ["udp://tracker.test:1337/announce"],
    };

    // --- helpers -----------------------------------------------------------------

    protected async Task WantOneEpisodeAsync() => await WantEpisodesAsync(1);

    // The show carries one episode that is already on the server, because a show with
    // nothing is one this plugin leaves alone - see RefreshWantedAsync. It sits after the
    // wanted ones so their numbers, which several tests assert on, stay 1..count.
    protected async Task WantEpisodesAsync(int count)
    {
        _library.Add(1, "Some Show", "/media/some-show",
        [
            .. Enumerable.Range(1, count).Select(number => (1, number, false)),
            (1, count + 1, true),
        ]);

        await Orchestrator().RefreshWantedAsync(CancellationToken.None);
    }

    protected async Task GrabOneAsync()
    {
        await WantOneEpisodeAsync();
        _search.Results = [Release()];
        await Orchestrator().SearchCycleAsync(CancellationToken.None);
    }

    protected static EngineTransfer Downloading(string hash, long done, long total, int peers) => new()
    {
        InfoHash = hash,
        State = EngineState.Downloading,
        BytesDone = done,
        BytesTotal = total,
        Peers = peers,
    };

    protected static EngineTransfer Completed(string hash, string folder) => new()
    {
        InfoHash = hash,
        State = EngineState.Completed,
        BytesDone = 1000,
        BytesTotal = 1000,
        CompletedFolder = folder,
    };

    protected static EngineTransfer Failed(string hash, string reason) => new()
    {
        InfoHash = hash,
        State = EngineState.Failed,
        FailureReason = reason,
    };

    protected sealed class FakeLibrary : ILibraryQuery
    {
        protected readonly List<LibraryShow> _shows = [];
        protected readonly Dictionary<int, List<LibraryEpisode>> _episodes = [];

        /// <summary>
        /// The status defaults to what the record itself defaults to, so a test that does
        /// not care about it exercises the same value a server too old to answer produces.
        /// </summary>
        public void Add(
            int showId,
            string title,
            string? folder,
            IEnumerable<(int Season, int Episode, bool HasFile)> episodes,
            ShowStatus status = ShowStatus.Unknown)
        {
            List<LibraryEpisode> list = [.. episodes.Select(episode =>
                new LibraryEpisode(showId, episode.Season, episode.Episode, $"Episode {episode.Episode}", null, episode.HasFile))];

            _shows.Add(new LibraryShow(showId, title, 2026, "lib-1", folder, list.Count, list.Count(e => e.HasFile))
            {
                Status = status,
            });

            _episodes[showId] = list;
        }

        /// <summary>Gives one episode an air date. Everything else stays undated, as most test libraries are.</summary>
        public void SetAirDate(int showId, int season, int episode, DateTimeOffset airs)
        {
            List<LibraryEpisode> list = _episodes[showId];
            int index = list.FindIndex(known => known.SeasonNumber == season && known.EpisodeNumber == episode);

            list[index] = list[index] with { AirDate = airs };
        }

        public Task<IReadOnlyList<LibraryShow>> GetShowsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LibraryShow>>(_shows);

        public Task<IReadOnlyList<LibraryEpisode>> GetEpisodesAsync(int showId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LibraryEpisode>>(_episodes.GetValueOrDefault(showId, []));

        public Task<IReadOnlyList<LibraryFile>> GetFilesAsync(int showId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LibraryFile>>([]);
    }

    protected sealed class FakeSearch : IReleaseSearch
    {
        public IReadOnlyList<ReleaseInfo> Results { get; set; } = [];

        /// <summary>
        /// Give each query its own release. A store keys grabs by info hash, so tests
        /// spanning several episodes need several torrents - handing them all the same
        /// hash would silently collapse them into one grab and prove nothing.
        /// </summary>
        public bool UniquePerQuery { get; set; }

        public List<SearchQuery> Queries { get; } = [];

        public Task<IReadOnlyList<ReleaseInfo>> SearchAsync(SearchQuery query, CancellationToken ct)
        {
            Queries.Add(query);

            if (!UniquePerQuery)
                return Task.FromResult(Results);

            string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(query.Text)));

            return Task.FromResult<IReadOnlyList<ReleaseInfo>>(
            [
                .. Results.Select(release => release with
                {
                    InfoHash = hash,
                    MagnetUri = $"magnet:?xt=urn:btih:{hash}",
                }),
            ]);
        }
    }

    /// <summary>Stands in for the sites: hands back where to get whatever it is asked about.</summary>
    protected sealed class FakeResolver : IReleaseResolver
    {
        public ReleaseInfo? Answer { get; set; }

        public List<string> Asked { get; } = [];

        public Task<ReleaseInfo?> ResolveAsync(ReleaseInfo announced, CancellationToken ct)
        {
            Asked.Add(announced.Title);
            return Task.FromResult(Answer);
        }
    }

    protected sealed class FakeFeed : IReleaseFeed
    {
        public IReadOnlyList<ReleaseInfo> Latest { get; set; } = [];

        public Task<IReadOnlyList<ReleaseInfo>> LatestAsync(CancellationToken ct) => Task.FromResult(Latest);
    }

    protected sealed class FakeChooser : IReleaseChooser
    {
        public bool Accept { get; set; } = true;

        public IReadOnlyList<ReleaseInfo> LastCandidates { get; private set; } = [];

        /// <summary>
        /// Whether the orchestrator was willing to spend a season's bytes on this search.
        /// It is the orchestrator's decision, not the chooser's - the chooser only obeys
        /// it - so this is where the decision is observable.
        /// </summary>
        public bool LastAllowedSeasonPacks { get; private set; }

        public ReleaseInfo? Choose(WantedEpisode episode, IReadOnlyList<ReleaseInfo> candidates, bool allowSeasonPacks)
        {
            LastCandidates = candidates;
            LastAllowedSeasonPacks = allowSeasonPacks;
            return Accept ? candidates.FirstOrDefault() : null;
        }
    }

    protected sealed class FakeEngine : ITorrentEngine
    {
        public List<TorrentRequest> Added { get; } = [];
        public List<string> Removed { get; } = [];

        /// <summary>Shared with <see cref="FakeIntake"/> when a test is about the order the two happen in.</summary>
        public List<string>? Trace { get; set; }
        public IReadOnlyList<EngineTransfer> Transfers { get; set; } = [];

        /// <summary>
        /// Throws on the next Add and then behaves. Stands in for a swarm that will not
        /// answer, a disk that will not take the file, or anything else the engine can
        /// fail on - the orchestrator's job is the same whichever it was.
        /// </summary>
        public Exception? ThrowOnceWith { get; set; }

        // The source doubles as the info hash so a test can tie a request to a transfer
        // without inventing a mapping the real engine would not have.
        public Task<string> AddAsync(TorrentRequest request, CancellationToken ct)
        {
            if (ThrowOnceWith is Exception failure)
            {
                ThrowOnceWith = null;
                throw failure;
            }

            Added.Add(request);
            return Task.FromResult(request.Source);
        }

        public Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken ct)
        {
            Removed.Add(infoHash);
            Trace?.Add($"released {infoHash}");
            return Task.CompletedTask;
        }

        public List<string> Paused { get; } = [];

        public List<string> Resumed { get; } = [];

        public Task PauseAsync(string infoHash, CancellationToken ct)
        {
            Paused.Add(infoHash);
            return Task.CompletedTask;
        }

        public Task ResumeAsync(string infoHash, CancellationToken ct)
        {
            Resumed.Add(infoHash);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EngineTransfer>> TransfersAsync(CancellationToken ct) =>
            Task.FromResult(Transfers);
    }

    protected sealed class FakeIntake : IIntakeHandoff
    {
        public bool Succeed { get; set; } = true;

        public List<(string Folder, EpisodeKey Key)> Moved { get; } = [];

        /// <inheritdoc cref="FakeEngine.Trace"/>
        public List<string>? Trace { get; set; }

        public Task<bool> MoveIntoIntakeAsync(string completedFolder, EpisodeKey key, CancellationToken ct)
        {
            Trace?.Add($"moved {completedFolder}");

            if (Succeed)
                Moved.Add((completedFolder, key));

            return Task.FromResult(Succeed);
        }
    }
    /// <summary>Grabs one pack and hands back the info hash the engine gave it.</summary>
    protected async Task<string> GrabAPackAsync(int episodes)
    {
        await WantEpisodesAsync(episodes);
        _search.Results = [Release("Some.Show.S01.1080p.WEB-DL", "packhash")];
        await Orchestrator().SearchCycleAsync(CancellationToken.None);

        return _engine.Added[0].Source;
    }
}

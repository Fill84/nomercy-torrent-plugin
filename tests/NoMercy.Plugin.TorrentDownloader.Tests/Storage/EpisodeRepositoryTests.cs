using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Storage;

/// <summary>
/// Against a real SQLite file in a temporary folder, not an in-memory
/// substitute: the upsert, the transaction and the delete are the behaviour
/// under test, and a fake store would be a second implementation of exactly the
/// part that could be wrong.
/// </summary>
public class EpisodeRepositoryTests : IAsyncLifetime
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));

    private Database _database = null!;
    private EpisodeRepository _episodes = null!;

    public async Task InitializeAsync()
    {
        _database = new(_folder);
        await _database.MigrateAsync(CancellationToken.None);
        _episodes = new(_database);
    }

    public Task DisposeAsync()
    {
        // The pool holds the file open, so it cannot be deleted until every
        // connection this test made is really gone.

        TemporaryFolder.Forget(_folder);

        return Task.CompletedTask;
    }

    /// <remarks>
    /// <strong>B1.</strong> Every field the library owns is rewritten from the
    /// derivation, state included. That is what makes <c>Unavailable</c>
    /// temporary: an episode given up on last night is derived as missing again
    /// this morning and gets another turn. 0.3.4 preserved the state instead
    /// and an episode that went unavailable once was invisible for ever.
    /// </remarks>
    [Fact]
    public async Task AnUnavailableEpisodeReturnsToMissingOnARefresh()
    {
        await _episodes.ReplaceAsync([Missing(1, 1, 1)], CancellationToken.None);
        await _episodes.MarkUnavailableAsync(new(1, 1, 1), CancellationToken.None);

        Assert.Equal(EpisodeState.Unavailable, (await All())[0].State);

        await _episodes.ReplaceAsync([Missing(1, 1, 1)], CancellationToken.None);

        Assert.Equal(EpisodeState.Missing, (await All())[0].State);
    }

    /// <remarks>
    /// The two things the library cannot tell us are the two things a refresh
    /// must not touch. Rewriting them would forget, every night, everything the
    /// plugin had learnt about how hard an episode is to find.
    /// </remarks>
    [Fact]
    public async Task AttemptsAndLastSearchedSurviveARefresh()
    {
        DateTimeOffset searched = new(2026, 8, 13, 4, 30, 0, TimeSpan.Zero);

        await _episodes.ReplaceAsync([Missing(1, 1, 1)], CancellationToken.None);
        await _episodes.RecordSearchAsync(new(1, 1, 1), searched, CancellationToken.None);
        await _episodes.RecordSearchAsync(new(1, 1, 1), searched, CancellationToken.None);

        await _episodes.ReplaceAsync([Missing(1, 1, 1)], CancellationToken.None);

        TrackedEpisode stored = (await All())[0];
        Assert.Equal(2, stored.Attempts);
        Assert.Equal(searched, stored.LastSearchAt);
    }

    /// <remarks>
    /// <strong>B2.</strong> Only a recorded search moves the count. In 0.3.4 a
    /// download that failed burned a search attempt, so three failed grabs
    /// exhausted an episode no search had ever gone badly for — the number went
    /// up, which looked like work. Nothing else in this repository can move it.
    /// </remarks>
    [Fact]
    public async Task NothingButARecordedSearchMovesTheAttemptCount()
    {
        await _episodes.ReplaceAsync([Missing(1, 1, 1)], CancellationToken.None);

        await _episodes.MarkUnavailableAsync(new(1, 1, 1), CancellationToken.None);
        await _episodes.ReplaceAsync([Missing(1, 1, 1)], CancellationToken.None);
        await _episodes.ReplaceAsync([Missing(1, 1, 1)], CancellationToken.None);

        Assert.Equal(0, (await All())[0].Attempts);

        await _episodes.RecordSearchAsync(new(1, 1, 1), DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Equal(1, (await All())[0].Attempts);
    }

    /// <remarks>
    /// Gone from the library is gone from here — the show was removed, or the
    /// episode now has a file. A row left behind would keep an episode in a
    /// queue for something already on disk.
    /// </remarks>
    [Fact]
    public async Task ARowForAnEpisodeTheLibraryNoLongerHasIsDeleted()
    {
        await _episodes.ReplaceAsync(
            [Missing(1, 1, 1), Missing(1, 1, 2), Missing(2, 3, 4)],
            CancellationToken.None);

        await _episodes.ReplaceAsync([Missing(1, 1, 2)], CancellationToken.None);

        Assert.Equal([new EpisodeKey(1, 1, 2)], (await All()).Select(episode => episode.Key));
    }

    /// <remarks>
    /// Everything the library owns is refreshed, not only the state: a show
    /// renamed on the server is renamed here, or a page would go on naming it
    /// the old way until somebody noticed.
    /// </remarks>
    [Fact]
    public async Task WhatTheLibrarySaysIsRewrittenEveryTime()
    {
        await _episodes.ReplaceAsync([Missing(1, 1, 1)], CancellationToken.None);

        await _episodes.ReplaceAsync(
            [
                Missing(1, 1, 1) with
                {
                    ShowTitle = "Renamed",
                    ShowYear = 2019,
                    Kind = LibraryKind.Anime,
                    EpisodeTitle = "A new title",
                    AirDate = new DateOnly(2020, 2, 2),
                    Absolute = 137,
                },
            ],
            CancellationToken.None);

        TrackedEpisode stored = (await All())[0];
        Assert.Equal("Renamed", stored.ShowTitle);
        Assert.Equal(2019, stored.ShowYear);
        Assert.Equal(LibraryKind.Anime, stored.Kind);
        Assert.Equal("A new title", stored.EpisodeTitle);
        Assert.Equal(new DateOnly(2020, 2, 2), stored.AirDate);
        Assert.Equal(137, stored.Absolute);
    }

    /// <remarks>
    /// Null crosses as null. An episode with no announced date is not one that
    /// aired at the epoch, and a year nobody knows is not the year nought.
    /// </remarks>
    [Fact]
    public async Task WhatIsNotKnownIsStoredAsNotKnown()
    {
        await _episodes.ReplaceAsync(
            [
                new TrackedEpisode(
                    new(1, 1, 1), "Silo", null, LibraryKind.Television, null, null, EpisodeState.NotAired),
            ],
            CancellationToken.None);

        TrackedEpisode stored = (await All())[0];
        Assert.Null(stored.ShowYear);
        Assert.Null(stored.EpisodeTitle);
        Assert.Null(stored.AirDate);
        Assert.Null(stored.Absolute);
        Assert.Null(stored.LastSearchAt);
        Assert.Equal(EpisodeState.NotAired, stored.State);
    }

    /// <remarks>
    /// A refresh that finds nothing empties the table. It is a derived cache:
    /// if the library has nothing for this plugin, neither has this plugin.
    /// </remarks>
    [Fact]
    public async Task ARefreshWithNothingInItEmptiesTheTable()
    {
        await _episodes.ReplaceAsync([Missing(1, 1, 1)], CancellationToken.None);

        await _episodes.ReplaceAsync([], CancellationToken.None);

        Assert.Empty(await All());
    }

    private Task<IReadOnlyList<TrackedEpisode>> All()
    {
        return _episodes.AllAsync(CancellationToken.None);
    }

    private static TrackedEpisode Missing(int show, int season, int episode)
    {
        return new(
            new(show, season, episode),
            "Silo",
            2023,
            LibraryKind.Television,
            "An episode",
            new DateOnly(2026, 1, 1),
            EpisodeState.Missing);
    }
}

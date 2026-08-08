// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Store;

/// <summary>
/// The behaviour every store must have. The file store inherits this suite, so the
/// in-memory store used by orchestrator tests cannot quietly drift from the real thing.
/// </summary>
public abstract class DownloadStoreContract
{
    protected abstract IDownloadStore Create();

    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private static WantedEpisode Episode(int season, int number, int show = 1) => new()
    {
        Key = new EpisodeKey(show, season, number),
        ShowTitle = "Some Show",
        EpisodeTitle = $"Episode {number}",
        AirDate = new DateOnly(2026, 8, 1),
    };

    private static Grab Grab(string infoHash, EpisodeKey key) => new()
    {
        InfoHash = infoHash,
        Key = key,
        ReleaseTitle = "Some.Show.S01E01.1080p.WEB-DL",
        Indexer = "site-a",
        SizeBytes = 2_000_000_000,
        GrabbedAt = Now,
    };

    [Fact]
    public async Task RefreshWantedAsync_WantsWhatTheLibraryIsMissing()
    {
        IDownloadStore store = Create();

        await store.RefreshWantedAsync([Episode(1, 1), Episode(1, 2)], CancellationToken.None);

        (await store.WantedAsync(10, CancellationToken.None)).Should().HaveCount(2);
    }

    [Fact]
    public async Task RefreshWantedAsync_StopsWantingAnEpisodeTheLibraryNowHas()
    {
        IDownloadStore store = Create();
        await store.RefreshWantedAsync([Episode(1, 1), Episode(1, 2)], CancellationToken.None);

        // The user dropped a file in by hand, so the library stops reporting it missing.
        await store.RefreshWantedAsync([Episode(1, 2)], CancellationToken.None);

        IReadOnlyList<WantedEpisode> wanted = await store.WantedAsync(10, CancellationToken.None);
        wanted.Should().ContainSingle();
        wanted[0].Key.Episode.Should().Be(2);
    }

    [Fact]
    public async Task RefreshWantedAsync_WantsAnEpisodeAgainWhenItsFileDisappears()
    {
        IDownloadStore store = Create();
        await store.RefreshWantedAsync([Episode(1, 1)], CancellationToken.None);
        await store.RefreshWantedAsync([], CancellationToken.None);

        await store.RefreshWantedAsync([Episode(1, 1)], CancellationToken.None);

        (await store.WantedAsync(10, CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task RefreshWantedAsync_KeepsWhatWasLearnedAboutAnEpisodeItAlreadyKnew()
    {
        IDownloadStore store = Create();
        await store.RefreshWantedAsync([Episode(1, 1)], CancellationToken.None);
        await store.MarkSearchedAsync(new EpisodeKey(1, 1, 1), Now, WantedState.Wanted, CancellationToken.None);

        await store.RefreshWantedAsync([Episode(1, 1)], CancellationToken.None);

        // A refresh that resets the attempt count restarts the back-off every cycle, and
        // the plugin hammers a release nobody is seeding forever.
        WantedEpisode? episode = await store.FindWantedAsync(new EpisodeKey(1, 1, 1), CancellationToken.None);
        episode!.SearchAttempts.Should().Be(1);
        episode.LastSearchedAt.Should().Be(Now);
    }

    [Fact]
    public async Task WantedAsync_LeavesOutWhatIsAlreadyGrabbedOrGivenUpOn()
    {
        IDownloadStore store = Create();
        await store.RefreshWantedAsync([Episode(1, 1), Episode(1, 2), Episode(1, 3)], CancellationToken.None);

        await store.MarkSearchedAsync(new EpisodeKey(1, 1, 1), Now, WantedState.Grabbed, CancellationToken.None);
        await store.MarkSearchedAsync(new EpisodeKey(1, 1, 2), Now, WantedState.Unavailable, CancellationToken.None);

        IReadOnlyList<WantedEpisode> wanted = await store.WantedAsync(10, CancellationToken.None);

        wanted.Should().ContainSingle().Which.Key.Episode.Should().Be(3);
    }

    [Fact]
    public async Task WantedAsync_TakesTheOnesLeastRecentlySearchedFirst()
    {
        IDownloadStore store = Create();
        await store.RefreshWantedAsync([Episode(1, 1), Episode(1, 2)], CancellationToken.None);

        await store.MarkSearchedAsync(new EpisodeKey(1, 1, 1), Now, WantedState.Wanted, CancellationToken.None);

        // Episode 2 has never been searched, so it goes first. Otherwise a big backlog
        // means the same few rows are looked at every cycle and the rest never are.
        IReadOnlyList<WantedEpisode> wanted = await store.WantedAsync(10, CancellationToken.None);

        wanted[0].Key.Episode.Should().Be(2);
    }

    [Fact]
    public async Task WantedAsync_HonoursTheLimitSoAFirstRunIsAStreamNotAFlood()
    {
        IDownloadStore store = Create();

        await store.RefreshWantedAsync([.. Enumerable.Range(1, 200).Select(number => Episode(1, number))], CancellationToken.None);

        (await store.WantedAsync(10, CancellationToken.None)).Should().HaveCount(10);
    }

    [Fact]
    public async Task AddGrabAsync_RecordsWhatWasChosenAndWhy()
    {
        IDownloadStore store = Create();
        EpisodeKey key = new(1, 1, 1);

        await store.AddGrabAsync(Grab("abc123", key), CancellationToken.None);

        Grab? found = await store.FindGrabAsync("abc123", CancellationToken.None);
        found.Should().NotBeNull();
        found!.Key.Should().Be(key);
        found.Indexer.Should().Be("site-a");
        found.State.Should().Be(GrabState.Grabbed);
    }

    [Fact]
    public async Task UpdateGrabAsync_WalksAGrabThroughItsLifetime()
    {
        IDownloadStore store = Create();
        await store.AddGrabAsync(Grab("abc123", new EpisodeKey(1, 1, 1)), CancellationToken.None);

        await store.UpdateGrabAsync("abc123", GrabState.Downloading, null, null, CancellationToken.None);
        (await store.FindGrabAsync("abc123", CancellationToken.None))!.State.Should().Be(GrabState.Downloading);

        await store.UpdateGrabAsync("abc123", GrabState.Imported, null, Now, CancellationToken.None);

        Grab? finished = await store.FindGrabAsync("abc123", CancellationToken.None);
        finished!.State.Should().Be(GrabState.Imported);
        finished.FinishedAt.Should().Be(Now);
    }

    [Fact]
    public async Task UpdateGrabAsync_KeepsTheReasonAFailureActuallyHad()
    {
        IDownloadStore store = Create();
        await store.AddGrabAsync(Grab("abc123", new EpisodeKey(1, 1, 1)), CancellationToken.None);

        await store.UpdateGrabAsync("abc123", GrabState.Failed, "no peers after 30 minutes", Now, CancellationToken.None);

        (await store.FindGrabAsync("abc123", CancellationToken.None))!.FailureReason
            .Should().Be("no peers after 30 minutes");
    }

    [Fact]
    public async Task ActiveGrabsAsync_LeavesOutTheFinishedAndTheFailed()
    {
        IDownloadStore store = Create();
        await store.AddGrabAsync(Grab("running", new EpisodeKey(1, 1, 1)), CancellationToken.None);
        await store.AddGrabAsync(Grab("done", new EpisodeKey(1, 1, 2)), CancellationToken.None);
        await store.AddGrabAsync(Grab("broken", new EpisodeKey(1, 1, 3)), CancellationToken.None);

        await store.UpdateGrabAsync("done", GrabState.Imported, null, Now, CancellationToken.None);
        await store.UpdateGrabAsync("broken", GrabState.Failed, "gave up", Now, CancellationToken.None);

        IReadOnlyList<Grab> active = await store.ActiveGrabsAsync(CancellationToken.None);

        active.Should().ContainSingle().Which.InfoHash.Should().Be("running");
    }

    [Fact]
    public async Task RecordTransferAsync_KeepsOneRowPerTorrentAndTheLatestProgress()
    {
        IDownloadStore store = Create();

        await store.RecordTransferAsync(new Transfer { InfoHash = "abc", BytesDone = 10, BytesTotal = 100, Peers = 4, UpdatedAt = Now }, CancellationToken.None);
        await store.RecordTransferAsync(new Transfer { InfoHash = "abc", BytesDone = 60, BytesTotal = 100, Peers = 9, UpdatedAt = Now }, CancellationToken.None);

        IReadOnlyList<Transfer> transfers = await store.TransfersAsync(CancellationToken.None);

        transfers.Should().ContainSingle();
        transfers[0].BytesDone.Should().Be(60);
        transfers[0].Progress.Should().Be(0.6);
    }

    [Fact]
    public async Task IsBlacklistedAsync_SkipsAReleaseThatFailed()
    {
        IDownloadStore store = Create();

        await store.BlacklistAsync(new BlacklistEntry
        {
            InfoHash = "badhash",
            ReleaseTitle = "Bad.Release.S01E01",
            Reason = "failed verification",
            AddedAt = Now,
            ExpiresAt = Now.AddDays(30),
        }, CancellationToken.None);

        (await store.IsBlacklistedAsync("badhash", "anything", Now, CancellationToken.None)).Should().BeTrue();
        (await store.IsBlacklistedAsync(null, "Bad.Release.S01E01", Now, CancellationToken.None)).Should().BeTrue();
        (await store.IsBlacklistedAsync("otherhash", "Good.Release", Now, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task IsBlacklistedAsync_LetsAnExpiredEntryBeTriedAgain()
    {
        IDownloadStore store = Create();

        await store.BlacklistAsync(new BlacklistEntry
        {
            InfoHash = "badhash",
            Reason = "no peers",
            AddedAt = Now,
            ExpiresAt = Now.AddDays(7),
        }, CancellationToken.None);

        // A release nobody was seeding in August may be fine in October. A permanent
        // blacklist rots and quietly shrinks what the plugin can ever find.
        (await store.IsBlacklistedAsync("badhash", "x", Now.AddDays(8), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task IsBlacklistedAsync_KeepsAPermanentEntryForever()
    {
        IDownloadStore store = Create();

        await store.BlacklistAsync(new BlacklistEntry
        {
            InfoHash = "malware",
            Reason = "imported as the wrong thing",
            AddedAt = Now,
            ExpiresAt = null,
        }, CancellationToken.None);

        (await store.IsBlacklistedAsync("malware", "x", Now.AddYears(5), CancellationToken.None)).Should().BeTrue();
    }
}

public class InMemoryDownloadStoreTests : DownloadStoreContract
{
    protected override IDownloadStore Create() => new InMemoryDownloadStore();
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Store;

/// <summary>The same suite the in-memory store passes, against a real file.</summary>
public class FileDownloadStoreContractTests : DownloadStoreContract, IDisposable
{
    private readonly TempFolder _folder = new();

    protected override IDownloadStore Create() => new FileDownloadStore(_folder.File("downloads.json"));

    public void Dispose() => _folder.Dispose();
}

/// <summary>What only a store that touches disk can get wrong.</summary>
public class FileDownloadStoreDurabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private static WantedEpisode Episode(int number) => new()
    {
        Key = new EpisodeKey("show-1", 1, number),
        ShowTitle = "Some Show",
        EpisodeTitle = $"Episode {number}",
        AirDate = new DateOnly(2026, 8, 1),
    };

    [Fact]
    public async Task State_SurvivesBeingReopened()
    {
        using TempFolder folder = new();
        string path = folder.File("downloads.json");

        FileDownloadStore first = new(path);
        await first.RefreshWantedAsync([Episode(1), Episode(2)], CancellationToken.None);
        await first.MarkSearchedAsync(new EpisodeKey("show-1", 1, 1), Now, WantedState.Grabbed, CancellationToken.None);
        await first.AddGrabAsync(new Grab
        {
            InfoHash = "abc123",
            Key = new EpisodeKey("show-1", 1, 1),
            ReleaseTitle = "Some.Show.S01E01.1080p",
            Indexer = "site-a",
            SizeBytes = 42,
            GrabbedAt = Now,
        }, CancellationToken.None);

        FileDownloadStore reopened = new(path);

        WantedEpisode? episode = await reopened.FindWantedAsync(new EpisodeKey("show-1", 1, 1), CancellationToken.None);
        episode!.State.Should().Be(WantedState.Grabbed);
        episode.SearchAttempts.Should().Be(1);
        episode.AirDate.Should().Be(new DateOnly(2026, 8, 1));

        Grab? grab = await reopened.FindGrabAsync("abc123", CancellationToken.None);
        grab!.ReleaseTitle.Should().Be("Some.Show.S01E01.1080p");
        grab.GrabbedAt.Should().Be(Now);
    }

    [Fact]
    public async Task AFreshStore_StartsEmptyRatherThanFailing()
    {
        using TempFolder folder = new();

        FileDownloadStore store = new(folder.File("nothing-here-yet.json"));

        (await store.WantedAsync(10, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnreadableFile_StartsOverInsteadOfRefusingToRun()
    {
        using TempFolder folder = new();
        string path = folder.File("downloads.json");
        await File.WriteAllTextAsync(path, "this is not the file you are looking for");

        FileDownloadStore store = new(path);

        // This file holds history and in-flight state, not the user's media. Losing it
        // costs a re-search; refusing to start costs the whole plugin.
        (await store.WantedAsync(10, CancellationToken.None)).Should().BeEmpty();

        await store.RefreshWantedAsync([Episode(1)], CancellationToken.None);
        (await store.WantedAsync(10, CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task ASave_LeavesNoTemporaryFileBehind()
    {
        using TempFolder folder = new();
        FileDownloadStore store = new(folder.File("downloads.json"));

        await store.RefreshWantedAsync([Episode(1)], CancellationToken.None);

        Directory.GetFiles(folder.Path).Should().ContainSingle();
    }

    [Fact]
    public async Task ConcurrentWrites_DoNotLoseEachOther()
    {
        using TempFolder folder = new();
        FileDownloadStore store = new(folder.File("downloads.json"));
        await store.RefreshWantedAsync([.. Enumerable.Range(1, 50).Select(Episode)], CancellationToken.None);

        // The orchestrator will mark several episodes at once. A store that reads,
        // mutates and writes without a gate loses all but the last of them.
        await Task.WhenAll(Enumerable.Range(1, 50).Select(number =>
            store.MarkSearchedAsync(new EpisodeKey("show-1", 1, number), Now, WantedState.Grabbed, CancellationToken.None)));

        (await store.WantedAsync(100, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task ItWritesTheWholeStateEachTimeSoTheFileIsNeverPartial()
    {
        using TempFolder folder = new();
        string path = folder.File("downloads.json");
        FileDownloadStore store = new(path);

        await store.RefreshWantedAsync([Episode(1), Episode(2), Episode(3)], CancellationToken.None);
        await store.BlacklistAsync(new BlacklistEntry { InfoHash = "bad", Reason = "no peers", AddedAt = Now }, CancellationToken.None);

        // Reading the file with a second store proves it is complete and self-contained,
        // not a delta that only makes sense to the process that wrote it.
        FileDownloadStore other = new(path);

        (await other.WantedAsync(10, CancellationToken.None)).Should().HaveCount(3);
        (await other.IsBlacklistedAsync("bad", "x", Now, CancellationToken.None)).Should().BeTrue();
    }
}

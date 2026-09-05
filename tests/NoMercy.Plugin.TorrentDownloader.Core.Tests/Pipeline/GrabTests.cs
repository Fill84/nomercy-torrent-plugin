using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// Handing a chosen copy to the torrent client.
/// </summary>
public class GrabTests
{
    /// <summary>Where a download would land, written once.</summary>
    private const string Incomplete = @"D:\incomplete";

    /// <remarks>
    /// The client takes it and answers what it will be known by. That hash is
    /// what everything afterwards — the transfers tick, staging, recovery —
    /// finds it again from.
    /// </remarks>
    [Fact]
    public async Task AGrabHandsTheMagnetOverAndComesBackWithTheHash()
    {
        FakeEngine engine = new();

        Grabbed grabbed = await new Grab(engine, Room(Terabyte), new ActivityJournal())
            .TakeAsync(Copy(), "D:\\incomplete", [], CancellationToken.None);

        Assert.Equal(GrabResult.Taken, grabbed.Result);
        Assert.Equal("92D8A3F6864911EF292B4BE0DD5286406396D2B3", grabbed.InfoHash);

        Assert.Equal("D:\\incomplete", engine.Asked!.DownloadFolder);
        // The size is not on the request. It is checked before one is built:
        // Grab.Room reads it off the copy and refuses when the disk cannot take
        // it, so carrying it again handed the engine a number it had no duty to
        // use and never did.
        Assert.Equal(GrabResult.Taken, grabbed.Result);
    }

    /// <remarks>
    /// Every tracker anybody named for it: what the site's magnet carried and
    /// the owner's own list, without duplicates. More trackers is a faster
    /// download and costs nothing, which is the whole reason every indexer is
    /// asked in the first place.
    /// </remarks>
    [Fact]
    public async Task TheMergedTrackersAndTheOwnersOwnListBothTravelWithIt()
    {
        FakeEngine engine = new();

        await new Grab(engine, Room(Terabyte), new ActivityJournal()).TakeAsync(
            Copy() with { Trackers = ["udp://site.example:80", "udp://both.example:80"] },
            "D:\\incomplete",
            ["udp://owner.example:6969", "UDP://BOTH.EXAMPLE:80"],
            CancellationToken.None);

        Assert.Equal(
            ["udp://both.example:80", "udp://owner.example:6969", "udp://site.example:80"],
            engine.Asked!.Trackers.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <remarks>
    /// Checked before anything is handed over. A torrent that fills the disk
    /// takes the media server with it: the same disk holds the library and the
    /// database.
    /// </remarks>
    [Fact]
    public async Task FreeSpaceIsCheckedFirstAndTheRefusalNamesBothNumbers()
    {
        FakeEngine engine = new();

        Grabbed refused = await new Grab(engine, Room(1_000_000_000), new ActivityJournal())
            .TakeAsync(Copy(), "D:\\incomplete", [], CancellationToken.None);

        Assert.Equal(GrabResult.NoRoom, refused.Result);

        // How much was needed and how much there is, because "not enough space"
        // tells the owner nothing they can act on.
        Assert.Contains("3.7 GB", refused.Reason!, StringComparison.Ordinal);
        Assert.Contains("953.7 MB", refused.Reason!, StringComparison.Ordinal);
        Assert.Contains("D:\\incomplete", refused.Reason!, StringComparison.Ordinal);

        // And nothing was handed over: first means first.
        Assert.Null(engine.Asked);
    }

    /// <remarks>
    /// Null is not nought. A share that will not say how much is free is not a
    /// share with no room, and refusing every grab on one would be a plugin
    /// that had quietly stopped working.
    /// </remarks>
    /// <remarks>
    /// <para>
    /// What this cycle has already taken counts against the disk. Free space is
    /// what the disk says now, and a torrent taken a moment ago has downloaded
    /// almost none of itself yet — so ten grabs in one cycle each measured
    /// against the same free space, every one of them passed, and together they
    /// filled the disk.
    /// </para>
    /// <para>
    /// That is the whole of what this check is for: a torrent that fills the
    /// disk takes the media server with it. Checking one at a time against a
    /// number that does not move yet is not checking.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WhatThisCycleHasAlreadyTakenCountsAgainstTheDisk()
    {
        FakeEngine engine = new();

        // Room for two of them and not a byte more.
        Grab grab = new(engine, Room(9_000_000_000), new ActivityJournal());

        Assert.Equal(
            GrabResult.Taken,
            (await grab.TakeAsync(Copy(), "D:\\incomplete", [], CancellationToken.None)).Result);

        Assert.Equal(
            GrabResult.Taken,
            (await grab.TakeAsync(Copy(), "D:\\incomplete", [], CancellationToken.None)).Result);

        Grabbed third = await grab.TakeAsync(Copy(), "D:\\incomplete", [], CancellationToken.None);

        Assert.Equal(GrabResult.NoRoom, third.Result);

        // And it says what is really left rather than what the disk says, which
        // is the number the owner would otherwise be arguing with.
        Assert.Contains("953.7 MB", third.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASpaceNobodyCanMeasureIsNotTakenForNoSpace()
    {
        FakeEngine engine = new();

        Grabbed grabbed = await new Grab(engine, Room(null), new ActivityJournal())
            .TakeAsync(Copy(), "\\\\share\\incomplete", [], CancellationToken.None);

        Assert.Equal(GrabResult.Taken, grabbed.Result);

        // Nor is a copy whose size nobody published: a site that gave no size
        // has not said the file is empty.
        Assert.Equal(
            GrabResult.Taken,
            (await new Grab(engine, Room(10), new ActivityJournal())
                .TakeAsync(Copy() with { SizeBytes = null }, "D:\\incomplete", [], CancellationToken.None)).Result);
    }

    /// <remarks>
    /// <strong>B2.</strong> 0.3.4 counted a failed grab as a search attempt, so
    /// three failures in a row exhausted the episode and it was never looked
    /// for again — while the attempt count made it look like work was
    /// happening. A grab that fails is the client's fault or the network's, and
    /// never the episode's.
    /// </remarks>
    [Fact]
    public async Task AGrabTheClientRefusesIsRecordedInItsOwnWordsAndBurnsNoSearchAttempt()
    {
        ActivityJournal journal = new();

        Grabbed refused = await new Grab(new RefusingEngine(), Room(Terabyte), journal)
            .TakeAsync(Copy(), "D:\\incomplete", [], CancellationToken.None);

        Assert.Equal(GrabResult.Refused, refused.Result);

        // The client's own words, whatever they were.
        Assert.Contains("not a magnet", refused.Reason!, StringComparison.Ordinal);

        Assert.Contains(
            journal.Snapshot().History,
            one => one.Outcome == ActivityOutcome.Failed
                   && one.Detail!.Contains("not a magnet", StringComparison.Ordinal));

        // Nor does running out of room, which is the owner's disk and not the
        // episode's fault either.
        // This used to assert an `Attempt` that was hard-wired false and
        // read by nobody, which proved nothing. The result is what the
        // cycle acts on.
        Assert.Equal(
            GrabResult.NoRoom,
            (await new Grab(new FakeEngine(), Room(1), journal)
                .TakeAsync(Copy(), Incomplete, [], CancellationToken.None)).Result);
    }

    /// <remarks>
    /// A copy that arrived with a hash and no magnet is still grabbable: a hash
    /// is a magnet. One with neither is not, and saying so beats handing the
    /// client an empty string.
    /// </remarks>
    [Fact]
    public async Task AHashWithoutAMagnetIsEnoughAndNeitherIsRefusedByName()
    {
        FakeEngine engine = new();

        await new Grab(engine, Room(Terabyte), new ActivityJournal()).TakeAsync(
            Copy() with { Magnet = null },
            "D:\\incomplete",
            [],
            CancellationToken.None);

        Assert.StartsWith("magnet:?xt=urn:btih:", engine.Asked!.Source, StringComparison.Ordinal);

        Grabbed nothing = await new Grab(engine, Room(Terabyte), new ActivityJournal()).TakeAsync(
            Copy() with { Magnet = null, InfoHash = null },
            "D:\\incomplete",
            [],
            CancellationToken.None);

        Assert.Equal(GrabResult.Refused, nothing.Result);
        Assert.Contains("no magnet", nothing.Reason!, StringComparison.Ordinal);
    }

    private const long Terabyte = 1024L * 1024 * 1024 * 1024;

    private static ReleaseCopy Copy()
    {
        return new(
            "Silo S03E06 1080p WEB-DL x265",
            "LimeTorrents",
            Priority: 1,
            InfoHash: "92D8A3F6864911EF292B4BE0DD5286406396D2B3",
            Magnet: "magnet:?xt=urn:btih:92D8A3F6864911EF292B4BE0DD5286406396D2B3",
            Seeders: 40,
            SizeBytes: 4_000_000_000);
    }

    private static IStorageSpace Room(long? free)
    {
        return new FakeSpace(free);
    }

    private sealed class FakeSpace(long? free) : IStorageSpace
    {
        public long? FreeBytes(string folder)
        {
            return free;
        }
    }

    /// <summary>A client that takes whatever it is given.</summary>
    private sealed class FakeEngine : ITorrentEngine
    {
        public TorrentRequest? Asked { get; private set; }

        public Task<TorrentHandle> AddAsync(TorrentRequest request, CancellationToken ct)
        {
            Asked = request;

            return Task.FromResult(new TorrentHandle("92D8A3F6864911EF292B4BE0DD5286406396D2B3", null));
        }

        public Task<IReadOnlyList<TorrentStatus>> StatusAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<TorrentStatus>>([]);
        }

        public Task PauseAsync(string infoHash, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task ResumeAsync(string infoHash, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TorrentFile>> FilesAsync(string infoHash, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<TorrentFile>>([]);
        }
    }

    /// <summary>And one that will not.</summary>
    private sealed class RefusingEngine : FakeEngineBase
    {
        public override Task<TorrentHandle> AddAsync(TorrentRequest request, CancellationToken ct)
        {
            throw new NotSupportedException("'something' is not a magnet, and this client takes nothing else yet.");
        }
    }

    /// <summary>The parts of the port a refusing client still has to have.</summary>
    private abstract class FakeEngineBase : ITorrentEngine
    {
        public abstract Task<TorrentHandle> AddAsync(TorrentRequest request, CancellationToken ct);

        public Task<IReadOnlyList<TorrentStatus>> StatusAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<TorrentStatus>>([]);
        }

        public Task PauseAsync(string infoHash, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task ResumeAsync(string infoHash, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TorrentFile>> FilesAsync(string infoHash, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<TorrentFile>>([]);
        }
    }
}

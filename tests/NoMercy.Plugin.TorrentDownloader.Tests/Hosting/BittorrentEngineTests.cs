using NoMercy.Plugin.TorrentDownloader.Bittorrent;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// The torrent client's shell: its lifetime, its port, and what it says about
/// a torrent it has taken on.
/// </summary>
public class BittorrentEngineTests
{
    /// <remarks>
    /// Started once and stopped once, whatever ticks in between. The client
    /// owns sockets and a port mapping; a second one would bind a port the
    /// first already has and report it as somebody else's.
    /// </remarks>
    [Fact]
    public void StartingItFourTimesBindsThePortOnce()
    {
        // Nought, so the operating system picks the number: what matters is
        // that four ticks leave one port and not four.
        using BittorrentEngine engine = new(0, new ActivityJournal(), new CapturingLogger());

        engine.Start();

        int? first = engine.Port;

        for (int tick = 0; tick < 3; tick++)
        {
            engine.Start();
        }

        Assert.NotNull(first);
        Assert.Equal(first, engine.Port);
        Assert.Null(engine.Failure);
    }

    /// <remarks>
    /// And a host is entitled to dispose twice. The second one is not allowed
    /// to throw on the way out of a shutdown.
    /// </remarks>
    [Fact]
    public void DisposingItTwiceIsSafe()
    {
        BittorrentEngine engine = new(0, new ActivityJournal(), new CapturingLogger());

        engine.Start();
        engine.Dispose();
        engine.Dispose();
    }

    /// <remarks>
    /// A port something else has is reported with its number and the client
    /// carries on. Taking the plugin down over it would cost the owner
    /// everything else it does, and outgoing connections still work.
    /// </remarks>
    [Fact]
    public void APortInUseIsReportedWithItsNumberAndDoesNotThrow()
    {
        // Held for the whole test: a port this process released is one another
        // test can take between the two lines.
        using ListenSockets held = ListenSockets.Bind(0);
        int port = held.Port;

        ActivityJournal journal = new();

        using BittorrentEngine engine = new(port, journal, new CapturingLogger());

        engine.Start();

        Assert.Null(engine.Port);
        Assert.Contains(port.ToString(), engine.Failure!, StringComparison.Ordinal);

        Assert.Contains(
            journal.Snapshot().History,
            entry => entry.Outcome == ActivityOutcome.Failed
                     && entry.Detail!.Contains(port.ToString(), StringComparison.Ordinal));
    }

    /// <remarks>
    /// A magnet just taken on is fetching its metadata, and that is not a shade
    /// of downloading: it has no name, no files and no size until the metadata
    /// arrives, and "nought per cent downloading" makes a torrent that will
    /// never resolve look like one about to start.
    /// </remarks>
    [Fact]
    public async Task AMagnetJustAddedIsFetchingItsMetadataAndNotDownloading()
    {
        using BittorrentEngine engine = Started();

        TorrentHandle handle = await engine.AddAsync(Request, CancellationToken.None);

        Assert.Equal("92D8A3F6864911EF292B4BE0DD5286406396D2B3", handle.InfoHash);

        TorrentStatus status = Assert.Single(await engine.StatusAsync(CancellationToken.None));

        Assert.Equal(TorrentState.FetchingMetadata, status.State);
        Assert.NotEqual(TorrentState.Downloading, status.State);

        // And every number says what it is rather than being drawn as nought.
        Assert.Null(status.Ratio);
        Assert.Null(status.Eta);
        Assert.Equal(0, status.BytesDone);

        // No file list at all until the metadata arrives: inventing one from
        // the name is how the wrong file gets staged.
        Assert.Empty(await engine.FilesAsync(handle.InfoHash, CancellationToken.None));
    }

    /// <remarks>
    /// Paused is its own state and stays that way until something resumes it.
    /// </remarks>
    [Fact]
    public async Task APausedTorrentIsPausedAndAResumedOneIsNot()
    {
        using BittorrentEngine engine = Started();

        TorrentHandle handle = await engine.AddAsync(Request, CancellationToken.None);

        await engine.PauseAsync(handle.InfoHash, CancellationToken.None);

        Assert.Equal(TorrentState.Paused, (await engine.StatusAsync(CancellationToken.None))[0].State);

        await engine.ResumeAsync(handle.InfoHash, CancellationToken.None);

        Assert.NotEqual(TorrentState.Paused, (await engine.StatusAsync(CancellationToken.None))[0].State);
    }

    /// <remarks>
    /// The same torrent offered twice is one torrent with more trackers, which
    /// is the whole reason every indexer is asked. Two rows for one info hash
    /// would be two clients downloading the same file into the same folder.
    /// </remarks>
    [Fact]
    public async Task TheSameHashAddedTwiceIsOneTorrent()
    {
        using BittorrentEngine engine = Started();

        TorrentHandle handle = await engine.AddAsync(Request, CancellationToken.None);
        await engine.AddAsync(Request with { Trackers = ["udp://elsewhere.example:6969/announce"] }, CancellationToken.None);

        Assert.Single(await engine.StatusAsync(CancellationToken.None));

        // And it is one torrent with everybody's trackers: the magnet's own,
        // the ones the first site merged, and the ones the second brought.
        Assert.Equal(
            ["udp://elsewhere.example:6969/announce", "udp://one.example:80", "udp://two.example:80"],
            engine.TrackersOf(handle.InfoHash).Order());
    }

    /// <remarks>
    /// Removed means gone from the client. What happens to the files is the
    /// caller's business and is asked for by name.
    /// </remarks>
    [Fact]
    public async Task ARemovedTorrentIsGone()
    {
        using BittorrentEngine engine = Started();

        TorrentHandle handle = await engine.AddAsync(Request, CancellationToken.None);

        await engine.RemoveAsync(handle.InfoHash, deleteFiles: false, CancellationToken.None);

        Assert.Empty(await engine.StatusAsync(CancellationToken.None));
    }

    /// <remarks>
    /// Anything that is not a magnet is refused by name. The port's
    /// documentation allows the address of a <c>.torrent</c> as well, and
    /// nothing in this plugin produces one — every copy the find stage chooses
    /// carries a hash, and a hash is a magnet. Half-supporting it would be a
    /// path nothing exercises until the day it matters.
    /// </remarks>
    [Fact]
    public async Task ASourceThatIsNotAMagnetIsRefusedByName()
    {
        using BittorrentEngine engine = Started();

        NotSupportedException refused = await Assert.ThrowsAsync<NotSupportedException>(
            () => engine.AddAsync(
                Request with { Source = "https://example.test/thing.torrent" },
                CancellationToken.None));

        Assert.Contains("thing.torrent", refused.Message, StringComparison.Ordinal);
    }

    private static readonly TorrentRequest Request = new(
        "magnet:?xt=urn:btih:92D8A3F6864911EF292B4BE0DD5286406396D2B3&dn=Silo+S03E06&tr=udp%3A%2F%2Fone.example%3A80",
        ["udp://two.example:80"],
        "C:\\downloads",
        4_388_742_440);

    private static BittorrentEngine Started()
    {
        BittorrentEngine engine = new(0, new ActivityJournal(), new CapturingLogger());

        engine.Start();

        return engine;
    }
}

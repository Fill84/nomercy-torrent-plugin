using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// When resume data is written, against a real folder on a real disk.
/// </summary>
/// <remarks>
/// Real files, because what is being asserted is that something readable is on
/// disk afterwards — and the one failure worth catching here is a file that was
/// half written when the power went, which a fake file system would never
/// produce.
/// </remarks>
public class ResumeKeeperTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nomercy-resume-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// Every interval and not oftener. Writing after every verified piece would
    /// have the disk busy with resume files rather than with the download.
    /// </remarks>
    [Fact]
    public void ItWritesOnTheIntervalAndNotBetweenThem()
    {
        FakeTimeProvider clock = new(Start);
        ResumeKeeper keeper = new(_folder, TimeSpan.FromMinutes(1), clock);

        Assert.True(keeper.Tick([Data(verified: 1)]));

        clock.Advance(TimeSpan.FromSeconds(59));

        Assert.False(keeper.Tick([Data(verified: 2)]));

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(keeper.Tick([Data(verified: 3)]));

        // And what is on disk is the last thing written, not the first.
        Assert.Equal(3, keeper.Load(Ubuntu)!.Verified.Count);
    }

    /// <remarks>
    /// A clean stop writes whatever the interval says. It is the one moment the
    /// data is certainly right, and skipping it because fifty seconds had
    /// passed rather than sixty would throw away everything verified since the
    /// last write.
    /// </remarks>
    [Fact]
    public void ACleanStopWritesWhateverTheIntervalSays()
    {
        FakeTimeProvider clock = new(Start);
        ResumeKeeper keeper = new(_folder, TimeSpan.FromMinutes(1), clock);

        keeper.Tick([Data(verified: 1)]);

        clock.Advance(TimeSpan.FromSeconds(10));

        keeper.Stop([Data(verified: 8)]);

        Assert.Equal(8, keeper.Load(Ubuntu)!.Verified.Count);
    }

    /// <remarks>
    /// A crash costs one interval of verification and not the whole torrent,
    /// which is the entire point of the file existing.
    /// </remarks>
    [Fact]
    public void WhatIsReloadedIsWhatWasVerified()
    {
        FakeTimeProvider clock = new(Start);
        ResumeKeeper keeper = new(_folder, TimeSpan.FromMinutes(1), clock);

        keeper.Stop([Data(verified: 5)]);

        ResumeData back = Assert.IsType<ResumeData>(new ResumeKeeper(_folder, TimeSpan.FromMinutes(1), clock).Load(Ubuntu));

        Assert.Equal(Ubuntu, back.InfoHash);
        Assert.Equal(5, back.Verified.Count);
        Assert.True(back.Verified.Has(0));
        Assert.False(back.Verified.Has(5));
    }

    /// <remarks>
    /// Nothing there is not an error: a torrent that has never been written, a
    /// folder that does not exist yet, a file somebody deleted. The answer to
    /// all three is to verify the torrent.
    /// </remarks>
    [Fact]
    public void NothingToLoadIsNotAnError()
    {
        ResumeKeeper keeper = new(_folder, TimeSpan.FromMinutes(1), new FakeTimeProvider(Start));

        Assert.Null(keeper.Load(Ubuntu));

        keeper.Stop([Data(verified: 1)]);

        Assert.NotNull(keeper.Load(Ubuntu));

        keeper.Forget(Ubuntu);

        Assert.Null(keeper.Load(Ubuntu));
    }

    /// <remarks>
    /// A file half written when the power went is worse than none — it parses
    /// far enough to be believed and then claims pieces are verified that are
    /// not. The new one is written under another name and moved into place, so
    /// the old one is good right up to the moment the new one is whole.
    /// </remarks>
    [Fact]
    public void TheOldFileIsGoodUntilTheNewOneIsWholeAndNothingIsLeftBehind()
    {
        FakeTimeProvider clock = new(Start);
        ResumeKeeper keeper = new(_folder, TimeSpan.FromMinutes(1), clock);

        keeper.Stop([Data(verified: 2)]);
        clock.Advance(TimeSpan.FromMinutes(5));
        keeper.Stop([Data(verified: 6)]);

        string[] files = [.. Directory.GetFiles(_folder).Select(Path.GetFileName)!];

        Assert.Equal([ResumeData.FileName(Ubuntu)], files);
        Assert.Equal(6, keeper.Load(Ubuntu)!.Verified.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private const string Ubuntu = "D160B8D8EA35A5B4E52837468FC8F03D55CEF1F7";

    private static DateTimeOffset Start => new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private static ResumeData Data(int verified)
    {
        Bitfield pieces = new(100);

        for (int piece = 0; piece < verified; piece++)
        {
            pieces.Set(piece);
        }

        return new(Ubuntu, pieces, Uploaded: 0, Downloaded: verified * 262144L, []);
    }
}

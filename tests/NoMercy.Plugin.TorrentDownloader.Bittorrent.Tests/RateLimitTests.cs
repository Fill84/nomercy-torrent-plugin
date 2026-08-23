using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// Rate limits, choking and when seeding stops.
/// </summary>
/// <remarks>
/// All three are decisions about time, and every one of them is put to a clock
/// a test moves by hand. A test that waited for a real second would be a test
/// that fails on a busy machine and passes on a quiet one, which is worse than
/// no test at all.
/// </remarks>
public class RateLimitTests
{
    /// <remarks>
    /// A megabyte a second means a megabyte in a second, and the second one
    /// waits for the second second. The bucket starts empty on purpose: one
    /// that started full would let a torrent take a whole second's worth the
    /// instant the plugin came up, which is when the owner is most likely to be
    /// watching something.
    /// </remarks>
    [Fact]
    public void AMegabyteASecondPassesAMegabyteInASecondAndNoMore()
    {
        FakeTimeProvider clock = new(Start);
        TokenBucket bucket = new(Megabyte, clock);

        Assert.Equal(0, bucket.Take(Megabyte));

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(Megabyte, bucket.Take(2 * Megabyte));
        Assert.Equal(0, bucket.Take(Megabyte));

        // And the second second is another megabyte and not two.
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(Megabyte, bucket.Take(4 * Megabyte));
    }

    /// <remarks>
    /// Half a second is half a megabyte. The bucket is not a per-second gate:
    /// a client that let nothing through until a whole second had passed would
    /// transfer in stutters a second apart.
    /// </remarks>
    [Fact]
    public void HalfASecondIsHalfTheAllowance()
    {
        FakeTimeProvider clock = new(Start);
        TokenBucket bucket = new(Megabyte, clock);

        clock.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal(Megabyte / 2, bucket.Take(Megabyte));
    }

    /// <remarks>
    /// An hour idle is not an hour of allowance. Without the cap a client that
    /// had been asleep would come back and saturate the line spending it.
    /// </remarks>
    [Fact]
    public void TimeSpentIdleDoesNotPileUp()
    {
        FakeTimeProvider clock = new(Start);
        TokenBucket bucket = new(Megabyte, clock);

        clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(Megabyte, bucket.Take(100 * Megabyte));
    }

    /// <remarks>
    /// Nought means no limit, from docs/06-torrent-client.md — not a limit of
    /// nothing, which would be a client that never transferred a byte.
    /// </remarks>
    [Fact]
    public void NoughtIsUnlimitedAndNotNothing()
    {
        TokenBucket bucket = new(0, new FakeTimeProvider(Start));

        Assert.True(bucket.Unlimited);
        Assert.Equal(100 * Megabyte, bucket.Take(100 * Megabyte));
    }

    /// <remarks>
    /// The lower of the two wins. A per-torrent limit above the global one
    /// would let three torrents each take the whole line; a global limit above
    /// a per-torrent one is not a licence to ignore what the owner said about
    /// that torrent.
    /// </remarks>
    [Fact]
    public void TheLowerOfTheGlobalAndPerTorrentLimitWins()
    {
        FakeTimeProvider clock = new(Start);
        RateLimits limits = new(clock);

        limits.Download.BytesPerSecond = 4 * Megabyte;
        limits.ForTorrent(Ubuntu, download: Megabyte, upload: 0);

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(Megabyte, limits.TakeDownload(Ubuntu, 4 * Megabyte));

        // The other way round: the global one is the lower, and it is the one
        // that decides.
        limits.Download.BytesPerSecond = Megabyte / 2;
        limits.ForTorrent(Archive, download: 4 * Megabyte, upload: 0);

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(Megabyte / 2, limits.TakeDownload(Archive, 4 * Megabyte));
    }

    /// <remarks>
    /// The global bucket is charged once for what really went, not once per
    /// torrent for what each asked. Charging it for the larger of the two would
    /// have two torrents at a megabyte each drain a four-megabyte global limit
    /// in half a second.
    /// </remarks>
    [Fact]
    public void TheGlobalBucketIsChargedForWhatWentAndNoMore()
    {
        FakeTimeProvider clock = new(Start);
        RateLimits limits = new(clock);

        limits.Download.BytesPerSecond = 4 * Megabyte;
        limits.ForTorrent(Ubuntu, download: Megabyte, upload: 0);
        limits.ForTorrent(Archive, download: Megabyte, upload: 0);

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(Megabyte, limits.TakeDownload(Ubuntu, 4 * Megabyte));
        Assert.Equal(Megabyte, limits.TakeDownload(Archive, 4 * Megabyte));

        // Two megabytes have gone, so two of the four are left for anybody.
        Assert.Equal(2 * Megabyte, limits.Download.Available(4 * Megabyte));
    }

    /// <remarks>
    /// Upload and download are separate buckets. A client that shared one would
    /// have a seeding torrent eat the limit the owner set on downloading.
    /// </remarks>
    [Fact]
    public void UploadAndDownloadAreCountedApart()
    {
        FakeTimeProvider clock = new(Start);
        RateLimits limits = new(clock);

        limits.Download.BytesPerSecond = Megabyte;
        limits.Upload.BytesPerSecond = Megabyte;

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(Megabyte, limits.TakeDownload(Ubuntu, Megabyte));
        Assert.Equal(Megabyte, limits.TakeUpload(Ubuntu, Megabyte));
    }

    /// <remarks>
    /// Changing a limit takes effect at once. The point of a limit is almost
    /// always that something is happening now, and an owner who has to restart
    /// the server to slow a download down has been given nothing.
    /// </remarks>
    [Fact]
    public void ChangingALimitTakesEffectWithoutARestart()
    {
        FakeTimeProvider clock = new(Start);
        RateLimits limits = new(clock);

        limits.Download.BytesPerSecond = Megabyte;

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(Megabyte, limits.TakeDownload(Ubuntu, 8 * Megabyte));

        limits.Download.BytesPerSecond = 8 * Megabyte;

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(8 * Megabyte, limits.TakeDownload(Ubuntu, 8 * Megabyte));

        // And down again, including all the way to unlimited.
        limits.Download.BytesPerSecond = 0;

        Assert.Equal(64 * Megabyte, limits.TakeDownload(Ubuntu, 64 * Megabyte));
    }

    /// <remarks>
    /// The ratio or the hours, whichever comes first, on a private torrent.
    /// </remarks>
    [Theory]
    [InlineData(0.5, 1, false)]
    [InlineData(1.0, 1, true)]
    [InlineData(1.5, 1, true)]
    [InlineData(0.1, 48, true)]
    [InlineData(0.1, 47, false)]
    public void SeedingStopsAtTheRatioOrTheHoursWhicheverComesFirst(double ratio, int hours, bool stopped)
    {
        SeedLimit limit = new(Ratio: 1.0, For: TimeSpan.FromHours(48));

        Assert.Equal(stopped, limit.Reached(priv: true, ratio, TimeSpan.FromHours(hours)));
    }

    /// <remarks>
    /// Nought is nobody asking for a limit, not a limit of nothing. A client
    /// that read it the other way would stop seeding the instant it finished.
    /// </remarks>
    [Fact]
    public void ALimitOfNoughtIsNoLimitAtAll()
    {
        SeedLimit none = new(Ratio: 0, For: TimeSpan.Zero);

        Assert.False(none.Reached(priv: true, ratio: 99, TimeSpan.FromDays(30)));

        // And one of the two on its own still works.
        Assert.True(new SeedLimit(Ratio: 2, For: TimeSpan.Zero).Reached(priv: true, 2.5, TimeSpan.FromMinutes(1)));
        Assert.True(new SeedLimit(Ratio: 0, For: TimeSpan.FromHours(1)).Reached(priv: true, 0, TimeSpan.FromHours(2)));
    }

    /// <remarks>
    /// <para>
    /// A public torrent is finished the moment it is complete, whatever the
    /// limits say. This client never uploads on a public swarm — the owner's
    /// rule, docs/06-torrent-client.md § Uploading — so staying in one gives
    /// nothing to anybody while costing a connection and a slot.
    /// </para>
    /// <para>
    /// A private one is where the limits mean something: there the tracker
    /// keeps an account of what the owner has given back, and the ratio the
    /// owner set is the one they want to reach.
    /// </para>
    /// </remarks>
    [Fact]
    public void APublicTorrentIsFinishedTheMomentItIsComplete()
    {
        SeedLimit limit = new(Ratio: 1.0, For: TimeSpan.FromHours(48));

        Assert.True(limit.Reached(priv: false, ratio: 0, TimeSpan.Zero));
        Assert.False(limit.Reached(priv: true, ratio: 0, TimeSpan.Zero));
    }

    /// <remarks>
    /// <para>
    /// <strong>A whole block or none of it.</strong> A caller holding a
    /// sixteen-kilobyte block cannot send eleven of them, so a limiter that
    /// only answers "this much may go" cannot be obeyed by one. That is why
    /// these buckets were written, tested and then wired to nothing: nothing
    /// could use them.
    /// </para>
    /// <para>
    /// Measured on a clock the test owns, so nothing here sleeps.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ABlockWaitsUntilTheWholeOfItMayGo()
    {
        FakeTimeProvider clock = new(Start);
        RateLimits limits = new(clock) { Download = { BytesPerSecond = Megabyte } };

        clock.Advance(TimeSpan.FromSeconds(1));

        // A megabyte is in the bucket, so a megabyte goes at once.
        await limits.PassAsync(downloading: true, Ubuntu, Megabyte, CancellationToken.None);

        // The next one has to wait for the bucket to fill again, and it waits
        // for the whole of itself rather than going half now and half later.
        Task passing = limits.PassAsync(downloading: true, Ubuntu, Megabyte, CancellationToken.None);

        Assert.False(passing.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(500));

        Assert.False(passing.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(600));

        await passing;
    }

    /// <remarks>
    /// No limit is no waiting at all, however much is asked for. A client that
    /// read nought as a limit of nothing would stop dead.
    /// </remarks>
    [Fact]
    public async Task WithNoLimitNothingWaits()
    {
        FakeTimeProvider clock = new(Start);
        RateLimits limits = new(clock);

        await limits.PassAsync(downloading: true, Ubuntu, 64 * Megabyte, CancellationToken.None);
        await limits.PassAsync(downloading: false, Ubuntu, 64 * Megabyte, CancellationToken.None);
    }

    private const long Megabyte = 1024 * 1024;

    private const string Ubuntu = "D160B8D8EA35A5B4E52837468FC8F03D55CEF1F7";

    private const string Archive = "E2720161FF77B42E61D15F4958134DEBAE8D0A96";

    private static DateTimeOffset Start => new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
}

using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// The deliveries from before this version, checked once against the name sources' search.
/// </summary>
/// <remarks>
/// The owner's rule of 15 October 2026. ViETNAM's S29E03 was delivered by 0.8.0, which wrote down nothing about
/// where a release came from, so after the update nothing marks it until it is checked. What the name sources
/// answer is <c>DeliveredCheckTests</c>, against their own pages; this is about asking once and keeping the
/// answer, so a run does not put the same paced question to four sites for every episode it ever delivered.
/// </remarks>
public sealed class DeliveredNamesTests : IDisposable
{
    private const string TelevisionLibrary = "01HQ5W4AVF30N10RT6XCF6AJHM";

    private const string AnimeLibrary = "01HQ5W4AVF30N10RT6XCF6AJHN";

    private const string Vietnam = "South Park S29E03 1080p HDTV DD 2.0 x264-ViETNAM";

    private const string Cakes = "South.Park.S29E02.1080p.WEB.H264-CAKES";

    private const string VietnamHash = "7445E4910B7A4D7E1F3C35D5A4A9C2E1B0C0FFEE";

    private const string CakesHash = "60AFDB530DD7CD04D1C36E142CC523A56E738DE8";

    private const string FansubHash = "D8FA5AC1CE27DAB1F07F0C93111C5763908182E8";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nomercy-delivered-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// <para>
    /// <strong>Checked once, and the answer kept.</strong> ViETNAM's release is one no source names, so it is
    /// written down as not named and its episode is marked; CAKES's is, and is written down as named. The next
    /// run asks nothing about either. An anime's delivery is never asked about at all: an anime is never
    /// replaced.
    /// </para>
    /// <para>
    /// Sabotage: not writing the answer down asks the sources again on every run; asking about every delivery
    /// asks about the anime's.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EachDeliveryIsCheckedOnceAndTheAnswerKept()
    {
        GrabRepository grabs = await Grabs();
        await Done(grabs, VietnamHash, Vietnam, new(2190, 29, 3));
        await Done(grabs, CakesHash, Cakes, new(2190, 29, 2));
        await Done(grabs, FansubHash, "[ToonsHub] Solo Leveling S02E01 1080p", new(88, 2, 1));

        NamesAnswering names = new(Cakes);
        DeliveredNames check = Check(grabs);

        Assert.Equal(1, await check.CheckAsync(names, CancellationToken.None));

        IReadOnlyList<StoredDownload> every = await grabs.EveryAsync(CancellationToken.None);

        Assert.False(Assert.Single(every, one => one.InfoHash == VietnamHash).Named);
        Assert.True(Assert.Single(every, one => one.InfoHash == CakesHash).Named);
        Assert.Null(Assert.Single(every, one => one.InfoHash == FansubHash).Named);
        Assert.Equal(2, names.Asked.Count);
        Assert.DoesNotContain(names.Asked, episode => episode.ShowTitle == "Solo Leveling");

        Assert.Equal(0, await check.CheckAsync(names, CancellationToken.None));
        Assert.Equal(2, names.Asked.Count);
    }

    /// <remarks>
    /// <strong>A source that did not answer leaves the delivery unchecked</strong>, and the next run asks
    /// again: its silence is not the release being unnamed. Sabotage: writing down "not named" when a source
    /// failed marks an episode nothing has been proved against.
    /// </remarks>
    [Fact]
    public async Task ADeliveryASourceCouldNotAnswerAboutIsAskedAgainNextRun()
    {
        GrabRepository grabs = await Grabs();
        await Done(grabs, VietnamHash, Vietnam, new(2190, 29, 3));

        NamesAnswering names = new() { Failing = true };
        DeliveredNames check = Check(grabs);

        Assert.Equal(0, await check.CheckAsync(names, CancellationToken.None));
        Assert.Null(Assert.Single(await grabs.EveryAsync(CancellationToken.None)).Named);

        await check.CheckAsync(names, CancellationToken.None);

        Assert.Equal(2, names.Asked.Count);
    }

    /// <summary>The name sources' searches, answering the same names for every episode, and counting what was asked.</summary>
    private sealed class NamesAnswering(params string[] names) : IReleaseNames
    {
        public List<TrackedEpisode> Asked { get; } = [];

        /// <summary>Whether a source fails to answer, as a site that does not answer does.</summary>
        public bool Failing { get; init; }

        public Task<FeedNamesTaken> ReadFeedsAsync(IReadOnlyList<TrackedEpisode> episodes, CancellationToken ct)
        {
            return Task.FromResult(FeedNamesTaken.None);
        }

        public Task<IReadOnlyList<SourceName>> NamesForAsync(TrackedEpisode episode, FeedNamesTaken taken, CancellationToken ct)
        {
            Asked.Add(episode);

            if (Failing)
            {
                taken.Fail("PreDB");
            }

            return Task.FromResult<IReadOnlyList<SourceName>>([.. names.Select(name => new SourceName(name, "PreDB"))]);
        }

        public Task<IReadOnlyList<SourceName>> PackNamesForAsync(IReadOnlyList<TrackedEpisode> season, FeedNamesTaken taken, CancellationToken ct)
        {
            Asked.AddRange(season);

            return Task.FromResult<IReadOnlyList<SourceName>>([]);
        }
    }

    /// <summary>A delivery 0.8.0 recorded: done, and nothing written down about where its release came from.</summary>
    private static async Task Done(GrabRepository grabs, string hash, string release, EpisodeKey episode)
    {
        await grabs.RecordAsync(
            episode,
            string.Empty,
            release,
            "TorrentBay",
            hash,
            $"magnet:?xt=urn:btih:{hash}",
            [episode],
            new DateTimeOffset(2026, 10, 15, 3, 5, 0, TimeSpan.Zero),
            CancellationToken.None);

        await grabs.StateAsync(hash, GrabState.Done, CancellationToken.None);
    }

    private static DeliveredNames Check(GrabRepository grabs)
    {
        FakeLibraryQuery query = new FakeLibraryQuery()
            .Library(TelevisionLibrary, "Series", "tv")
            .Library(AnimeLibrary, "Anime", "anime")
            .Show(2190, "South Park", TelevisionLibrary, year: 1997, folder: "/South.Park.(1997)")
            .Episode(2190, 29, 2, hasFile: true, airDate: new DateTime(2026, 10, 8))
            .Episode(2190, 29, 3, hasFile: true, airDate: new DateTime(2026, 10, 15))
            .Show(88, "Solo Leveling", AnimeLibrary, year: 2024, folder: "/Solo.Leveling.(2024)")
            .Episode(88, 2, 1, hasFile: true, airDate: new DateTime(2025, 1, 5));

        return new(grabs, new HostLibrary(query), AppliedToEveryShow.Searched, new CapturingLogger());
    }

    private async Task<GrabRepository> Grabs()
    {
        Store database = new(_root);

        await database.MigrateAsync(CancellationToken.None);

        return new(database);
    }

    public void Dispose()
    {
        TemporaryFolder.Forget(_root);
    }
}

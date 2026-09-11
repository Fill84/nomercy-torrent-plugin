using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// One torrent, six indexers, and the trackers that have to travel with it.
/// </summary>
/// <remarks>
/// <para>
/// <c>Silo S03E06 XviD-AFG</c>, info hash
/// <c>60207FB3AE7877C8C76DDD27A07C385E5047783C</c>, as six sites really
/// answered for it on 10 September 2026. Every page here is that day's capture.
/// </para>
/// <para>
/// <strong>What this exists to stop coming back.</strong> Measured that
/// morning: over four episodes and four sites, not one of a hundred and eighty
/// merged torrents reached the torrent client carrying a single tracker. The
/// client was handed <c>magnet:?xt=urn:btih:…&amp;dn=…</c> and nothing else, so
/// the swarm could only ever be found through the DHT — which is why downloads
/// sat at "fetching metadata" with no peer and no seed. Every tracker was on
/// the row's own page, and the row's own page was never read whenever the
/// listing had already given a hash.
/// </para>
/// </remarks>
public class EveryTrackerEverySiteKnewTests
{
    private const string Release = "Silo S03E06 XviD-AFG";
    private const string Hash = "60207FB3AE7877C8C76DDD27A07C385E5047783C";

    /// <remarks>
    /// The four sites that publish a magnet on the row's page hold 20, 19, 7
    /// and 6 trackers between them, and they overlap. What has to come out is
    /// the union — every distinct one, once — on a copy that arrived carrying
    /// none.
    /// </remarks>
    [Fact]
    public async Task TheTorrentCarriesEveryTrackerEveryIndexerKnew()
    {
        FakeFetch fetch = Answering();

        ReleaseCopy resolved = await Finding(fetch).ResolveAsync(Merged(), CancellationToken.None);

        string[] expected =
        [
            .. new[]
                {
                    "silo6afg-row-limetorrents.html",
                    "silo6afg-row-torrentdownloads.html",
                    "silo6afg-row-eztv.html",
                    "silo6afg-row-torrentz2.html",
                }
                .Select(page => Magnets.TrackersOf(DetailPage.Read(Capture.Fixture(page), Release)!.Value.Magnet))
                .SelectMany(trackers => trackers)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];

        // The captures really do carry them, or this would assert nothing.
        Assert.True(expected.Length > 20, $"the captures hold {expected.Length} distinct trackers");

        Assert.Equal(
            expected.Order(StringComparer.OrdinalIgnoreCase),
            resolved.Trackers.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <remarks>
    /// <strong>C3.</strong> One request per indexer that has the torrent, and
    /// none per row. Following every row of every answer is a request per row
    /// per episode, which is how a plugin gets itself banned from a site.
    /// </remarks>
    [Fact]
    public async Task EachIndexerIsAskedOnceAndOnlyForTheReleaseBeingTaken()
    {
        FakeFetch fetch = Answering();

        await Finding(fetch).ResolveAsync(Merged(), CancellationToken.None);

        // Five: the six routes less The Pirate Bay, which publishes no page at
        // all. TorrentBay's is asked and answers no magnet, which is still one
        // ask and not two.
        Assert.Equal(5, fetch.Asked.Count);
        Assert.Equal(fetch.Asked.Count, fetch.Asked.Distinct().Count());
    }

    /// <remarks>
    /// The reason the row pages used not to be read at all: The Pirate Bay
    /// answers with a hash and no page, and while its rows were tried page-first
    /// every one of them came back unreachable — the highest-priority indexer in
    /// the catalogue could not produce a single download. It keeps working
    /// exactly as it did.
    /// </remarks>
    [Fact]
    public async Task ACopyWhoseOnlySiteHasNoPageIsStillReachableFromItsHash()
    {
        FakeFetch fetch = new();

        ReleaseCopy resolved = await Finding(fetch).ResolveAsync(
            new ReleaseCopy(Release, "The Pirate Bay", 45, Hash)
            {
                Routes = [new("The Pirate Bay")],
            },
            CancellationToken.None);

        Assert.Empty(fetch.Asked);
        Assert.Equal($"magnet:?xt=urn:btih:{Hash}&dn={Uri.EscapeDataString(Release)}", resolved.Magnet);
        Assert.Empty(resolved.Trackers);
    }

    /// <remarks>
    /// A site that answers nothing is one site. What the others said is still
    /// the answer, and the torrent is still taken.
    /// </remarks>
    [Fact]
    public async Task OneSiteFailingCostsOnlyItsOwnTrackers()
    {
        FakeFetch fetch = Answering();

        fetch.Fails(
            "https://www.limetorrents.lol/Silo-S03E06-XviD-AFG-torrent-19877022.html",
            FetchOutcome.Refused,
            "it did not answer");

        ReleaseCopy resolved = await Finding(fetch).ResolveAsync(Merged(), CancellationToken.None);

        Assert.NotNull(resolved.Magnet);
        Assert.NotEmpty(resolved.Trackers);

        // And not one of LimeTorrents' own that nobody else published.
        string[] limeOnly =
        [
            .. Magnets
                .TrackersOf(DetailPage.Read(Capture.Fixture("silo6afg-row-limetorrents.html"), Release)!.Value.Magnet)
                .Except(
                    Magnets.TrackersOf(DetailPage.Read(Capture.Fixture("silo6afg-row-eztv.html"), Release)!.Value.Magnet)
                        .Concat(Magnets.TrackersOf(DetailPage.Read(Capture.Fixture("silo6afg-row-torrentz2.html"), Release)!.Value.Magnet))
                        .Concat(Magnets.TrackersOf(DetailPage.Read(Capture.Fixture("silo6afg-row-torrentdownloads.html"), Release)!.Value.Magnet)),
                    StringComparer.OrdinalIgnoreCase),
        ];

        Assert.NotEmpty(limeOnly);
        Assert.DoesNotContain(limeOnly[0], resolved.Trackers, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The torrent as the merge really produced it that day.</summary>
    private static ReleaseCopy Merged()
    {
        return new ReleaseCopy(Release, "The Pirate Bay", 45, Hash, Seeders: 144)
        {
            Routes =
            [
                new("The Pirate Bay"),
                new("LimeTorrents", new("https://www.limetorrents.lol/Silo-S03E06-XviD-AFG-torrent-19877022.html")),
                new(
                    "TorrentBay",
                    new("https://extranet.torrentbay.st/silo-s03e06-xvid-afg-eztv-21152818/")),
                new("EZTV", new("https://eztvx.to/ep/3134563/silo-s03e06-xvid-afg/?d=")),
                new("Torrentz2", new("https://torrentz2.nz/torrent/6a771b12b4102f84ff095fd3")),
                new(
                    "TorrentDownloads",
                    new("https://www.torrentdownloads.pro/torrent/1707076497/Silo-S03E06-XviD-AFG%5BEZTVx-to%5D-avi")),
            ],
        };
    }

    private static FakeFetch Answering()
    {
        FakeFetch fetch = new();

        fetch.Answers(
            "https://www.limetorrents.lol/Silo-S03E06-XviD-AFG-torrent-19877022.html",
            Capture.Fixture("silo6afg-row-limetorrents.html"));
        fetch.Answers(
            "https://extranet.torrentbay.st/silo-s03e06-xvid-afg-eztv-21152818/",
            Capture.Fixture("silo6afg-row-torrentbay.html"));
        fetch.Answers(
            "https://eztvx.to/ep/3134563/silo-s03e06-xvid-afg/?d=",
            Capture.Fixture("silo6afg-row-eztv.html"));
        fetch.Answers(
            "https://torrentz2.nz/torrent/6a771b12b4102f84ff095fd3",
            Capture.Fixture("silo6afg-row-torrentz2.html"));
        fetch.Answers(
            "https://www.torrentdownloads.pro/torrent/1707076497/Silo-S03E06-XviD-AFG%5BEZTVx-to%5D-avi",
            Capture.Fixture("silo6afg-row-torrentdownloads.html"));

        return fetch;
    }

    private static Find Finding(FakeFetch fetch)
    {
        return new(
            SourceCatalogue.Build(Sources, [], []),
            fetch,
            Readers.Shipped(),
            new ActivityJournal());
    }

    private static readonly SourceDefinition[] Sources =
    [
        new("The Pirate Bay", "apibay", "https://apibay.org/q.php?q={query}&cat=") { Priority = 45 },
        new("LimeTorrents", "site", "https://www.limetorrents.lol/search/all/{query}/") { Priority = 35 },
        new("TorrentBay", "site", "https://extranet.torrentbay.st/search/{query}/")
        {
            Reader = "torrentbay",
            Gated = true,
            Priority = 30,
        },
        new("EZTV", "site", "https://eztvx.to/search/{query}") { Reader = "eztv", Gated = true, Priority = 30 },
        new("Torrentz2", "site", "https://torrentz2.nz/search?q={query}") { Reader = "torrentz2", Priority = 25 },
        new("TorrentDownloads", "site", "https://www.torrentdownloads.pro/search/?search={query}")
        {
            Reader = "torrentdownloads",
            Priority = 25,
        },
    ];
}

using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Sources;

/// <summary>
/// Every reader against the page its site really answered, captured with
/// <c>tools/Capture</c> on 14 August 2026.
/// </summary>
/// <remarks>
/// Never a hand-written sample. Readers tested against invented markup avoided
/// every real case in 0.3.4: a show called <em>Greek</em> read as a
/// Greek-language release, a diacritic tokenised into fragments, and a site's
/// own tag appended to every title. All of it passed.
/// </remarks>
public class ReaderTests
{
    /// <remarks>
    /// <strong>E5.</strong> Sites were declared dead too fast. Every source has
    /// a fixture and a non-zero row count, so "it answers nothing" has to be
    /// demonstrated rather than assumed.
    /// </remarks>
    [Fact]
    public void The1337xListingYieldsItsRowsWithTheirOwnPages()
    {
        IReadOnlyList<SourceRow> rows = new X1337Reader().Read(
            Fixture("1337x"),
            new("https://www.1337x.to/sort-category-search/Silo+S03E06/TV/time/desc/1/"));

        Assert.Equal(9, rows.Count);

        SourceRow first = rows[0];
        Assert.Equal("Silo.S03E06.The.Drive.2160p.ATVP.WEB-DL.ITA.ENG.DDP5.1.Atmos.DV.HDR.H.265-G66.mkv", first.Title);
        Assert.Equal(
            "https://www.1337x.to/torrent/6701056/Silo-S03E06-The-Drive-2160p-ATVP-WEB-DL-ITA-ENG-DDP5-1-Atmos-DV-HDR-H-265-G66-mkv/",
            first.DetailUrl?.ToString());
        Assert.Equal(84, first.Seeders);
        Assert.Equal(35, first.Leechers);

        // 9.6 GB, and the seed count nested inside that same cell is not it.
        Assert.Equal((long)(9.6 * 1024 * 1024 * 1024), first.SizeBytes);

        // The listing carries no magnet at all; the row's own page is the route.
        Assert.All(rows, row => Assert.Null(row.Magnet));
        Assert.All(rows, row => Assert.NotNull(row.DetailUrl));
    }

    /// <remarks>
    /// Every title ends in the site's own tag and it has to go, or nothing
    /// matches a release name. <c>docs/05-sources.md</c> says <c>[eztv.re]</c>;
    /// the page as captured says <c>[eztv]</c>. Both are stripped.
    /// </remarks>
    [Fact]
    public void TheEztvListingHasItsOwnTagStrippedFromEveryTitle()
    {
        IReadOnlyList<SourceRow> rows = new EztvReader().Read(
            Fixture("eztv"),
            new("https://eztvx.to/search/Silo+S03E06"));

        Assert.Equal(6, rows.Count);
        Assert.Equal("Silo S03E06 1080p HEVC x265-MeGusta", rows[0].Title);
        Assert.Equal("https://eztvx.to/ep/3134565/silo-s03e06-1080p-hevc-x265-megusta/?d=", rows[0].DetailUrl?.ToString());
        Assert.Equal(491L * 1024 * 1024, rows[0].SizeBytes);

        Assert.All(rows, row => Assert.DoesNotContain("[eztv", row.Title, StringComparison.OrdinalIgnoreCase));
    }

    /// <remarks>
    /// The name is cut into fragments by a span colouring the matched words, so
    /// the anchor is read whole and stripped. Joining its text nodes glues the
    /// words together — which is how a release group was lost in 0.3.4.
    /// </remarks>
    [Fact]
    public void TheKickassListingJoinsTheHighlightedFragmentsOfEachName()
    {
        IReadOnlyList<SourceRow> rows = new KickassReader().Read(
            Fixture("kickasstorrents"),
            new("https://katcr.to/usearch/Silo+S03E06/"));

        Assert.Equal(8, rows.Count);

        // "Silo" is inside a <strong class="red"> and the rest is not.
        Assert.Equal(
            "Silo S03E06 The Drive 1080p ATVP WEB-DL ITA ENG DDP5 1 Atmos H 265-G66 mkv",
            rows[0].Title);
        Assert.Equal(
            "https://katcr.to/silo-s03e06-the-drive-1080p-atvp-web-dl-ita-eng-ddp5-1-atmos-h-265-g66-mkv-t6701055.html",
            rows[0].DetailUrl?.ToString());
        Assert.Equal(41, rows[0].Seeders);
        Assert.Equal(6, rows[0].Leechers);
    }

    /// <remarks>
    /// <strong>E4, as the site behaves today.</strong> The failure said a search
    /// for a full release name is answered with that release's own page. It is
    /// not, any more: the capture is a listing with one row in it. The reader's
    /// fallback for the redirect stays — a site that did this once may do it
    /// again — but this is what the site does now, and the reader has to handle
    /// it as an ordinary listing.
    /// </remarks>
    [Fact]
    public void AFullReleaseNameIsAnsweredWithAOneRowListing()
    {
        IReadOnlyList<SourceRow> rows = new KickassReader().Read(
            Fixture("kickasstorrents-full-name"),
            new("https://katcr.to/usearch/Silo+S03E06+The+Drive/"));

        SourceRow only = Assert.Single(rows);

        Assert.Contains("Silo", only.Title, StringComparison.Ordinal);
        Assert.NotNull(only.DetailUrl);
    }

    /// <remarks>
    /// LimeTorrents publishes a hashed <c>.torrent</c> address on the listing,
    /// which is a route to the torrent and an info hash in one — so the generic
    /// reader handles it, and a source gets that reader by naming none.
    /// </remarks>
    [Fact]
    public void TheGenericReaderTakesTheHashedTorrentLinkAndTheRowsOwnName()
    {
        IReadOnlyList<SourceRow> rows = new GenericReader().Read(
            Fixture("limetorrents"),
            new("https://www.limetorrents.lol/search/all/Silo+S03E06/"));

        Assert.NotEmpty(rows);

        SourceRow first = rows[0];
        Assert.Equal("Silo S03E06 1080p WEB H264-CAKES", first.Title);
        Assert.Equal("92D8A3F6864911EF292B4BE0DD5286406396D2B3", first.InfoHash);
        Assert.Equal(
            "https://www.limetorrents.lol/Silo-S03E06-1080p-WEB-H264-CAKES-torrent-19877003.html",
            first.DetailUrl?.ToString());
        Assert.Equal(4417, first.Seeders);
        Assert.Equal(2170, first.Leechers);
    }

    /// <remarks>
    /// A page with nothing on it answers nothing, rather than one empty row.
    /// </remarks>
    [Fact]
    public void APageWithNoRowsAnswersNoRows()
    {
        Uri from = new("https://www.1337x.to/search/nothing/");

        Assert.Empty(new X1337Reader().Read("<html><body>No results were returned</body></html>", from));
        Assert.Empty(new EztvReader().Read("<html><body></body></html>", from));
        Assert.Empty(new GenericReader().Read("<html><body><tr><td>a header</td></tr></body></html>", from));
    }

    private static string Fixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine(directory!.FullName, "tests", "fixtures", $"{name}.html"));
    }
}

public class ReaderRegistryTests
{
    /// <remarks>
    /// <strong>C4.</strong> Two readers were missing from 0.3.4's registry and
    /// both fell through to the generic one, which answers rows on some pages —
    /// so nothing looked broken. A name that resolves to nothing is caught
    /// here; a name that silently resolves to the generic reader is the fault
    /// itself, so <see cref="Readers.Named"/> answers null rather than falling
    /// back.
    /// </remarks>
    [Fact]
    public void AReaderNameNothingAnswersToResolvesToNothingRatherThanTheGenericOne()
    {
        Readers readers = All();

        // A site nobody has written a reader for. Every name the catalogue
        // really uses is checked by the test below.
        Assert.Null(readers.Named("a-site-nobody-has-written-a-reader-for"));
        Assert.NotNull(readers.Named("1337x"));
        Assert.IsType<GenericReader>(readers.For(new("LimeTorrents", "site", "https://x.test/{query}")));
    }

    /// <remarks>
    /// <strong>C4, in full.</strong> Every reader name in <c>sources.json</c>
    /// resolves to a reader written for that site. The catalogue is read from
    /// the file that ships rather than from a list written here, so a source
    /// added with a reader nobody wrote fails this test on the day it is added
    /// — which is the day it can still be fixed cheaply.
    /// </remarks>
    [Fact]
    public void EveryReaderNameTheCatalogueUsesResolvesToANonGenericReader()
    {
        Readers readers = All();

        string[] named = [.. Catalogue()
            .Select(source => source.Reader)
            .Where(reader => reader is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)!];

        Assert.NotEmpty(named);

        foreach (string name in named)
        {
            ISourceReader? reader = readers.Named(name);

            Assert.True(reader is not null, $"The catalogue names a reader '{name}' that nothing answers to.");
            Assert.IsNotType<GenericReader>(reader);
        }
    }

    /// <summary>The shipped catalogue, read out of the file that deploys.</summary>
    private static IEnumerable<SourceDefinition> Catalogue()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        string json = File.ReadAllText(Path.Combine(
            directory!.FullName,
            "src",
            "NoMercy.Plugin.TorrentDownloader",
            "sources.json"));

        // Only the reader names are wanted, and Core cannot see the loader that
        // parses the rest of it.
        return System.Text.RegularExpressions.Regex
            .Matches(json, @"""reader""\s*:\s*""([^""]+)""")
            .Select(match => new SourceDefinition("read from the file", "site", "https://x.test/{query}")
            {
                Reader = match.Groups[1].Value,
            });
    }

    private static Readers All()
    {
        return new(
            new GenericReader(),
            new X1337Reader(),
            new EztvReader(),
            new KickassReader(),
            new TorrentBayReader(),
            new TorrentGalaxyReader(),
            new Torrentz2Reader(),
            new TorrentDownloadsReader(),
            new TorrentFunkReader());
    }
}

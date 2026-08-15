using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Sources;

/// <summary>
/// The other five readers, each against the page its site really sent,
/// captured on 15 August 2026.
/// </summary>
public class ReaderTests2
{
    /// <remarks>
    /// <strong>D4.</strong> <c>[GeneratedRegex]</c> was measured returning zero
    /// matches on this very site where the identical inline expression returned
    /// fifty — and zero rows is exactly what a site with nothing looks like. So
    /// the expressions are <c>static readonly Regex</c> and the row count is
    /// asserted against the real capture.
    /// </remarks>
    [Fact]
    public void TorrentBayAnswersRowsOnTheRealCapture()
    {
        IReadOnlyList<SourceRow> rows = new TorrentBayReader().Read(
            Fixture("torrentbay"),
            new("https://extranet.torrentbay.st/browse/?q=Silo+S03E06"));

        Assert.NotEmpty(rows);

        SourceRow first = rows[0];

        // The name is cut into spans; read whole and stripped it comes back
        // with its words apart and its group intact.
        Assert.Equal("Silo S03E06 1080p WEB H264-CAKES EZTV", first.Title);
        Assert.Equal(
            "https://extranet.torrentbay.st/silo-s03e06-1080p-web-h264-cakes-eztv-21152668/",
            first.DetailUrl?.ToString());
        Assert.Equal(1971, first.Leechers);
    }

    /// <remarks>
    /// The magnet is not on the page: the site's own script fetches it from an
    /// endpoint this page never names, and each row carries only the id it
    /// would be asked for. A row with no id cannot be asked, and asking without
    /// one earns a refusal that reads like the site's.
    /// </remarks>
    [Fact]
    public void ATorrentBayRowCarriesTheIdItsMagnetWouldBeAskedFor()
    {
        string body = Fixture("torrentbay");

        Assert.Equal("21152668", TorrentBayReader.MagnetIdOf(body));
        Assert.Null(TorrentBayReader.MagnetIdOf("<tr><td>a row with no button</td></tr>"));

        Assert.All(
            new TorrentBayReader().Read(body, new("https://extranet.torrentbay.st/browse/")),
            row => Assert.Null(row.Magnet));
    }

    /// <remarks>
    /// <strong>E6.</strong> A dozen forty-hex strings on this page are element
    /// ids, not info hashes. Taking one would attach a stranger's hash to a
    /// release — the capture holds seven distinct ones and not a single magnet.
    /// </remarks>
    [Fact]
    public void TorrentGalaxyTakesNoHashFromAPageFullOfThingsThatLookLikeOne()
    {
        string body = Fixture("torrentgalaxy");

        IReadOnlyList<SourceRow> rows = new TorrentGalaxyReader().Read(
            body,
            new("https://torrentgalaxy.one/get-posts/keywords:Silo%20S03E06/"));

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Null(row.InfoHash));
        Assert.All(rows, row => Assert.Null(row.Magnet));

        // And the page really is full of them, so the rule has something to
        // refuse rather than nothing.
        Assert.True(
            System.Text.RegularExpressions.Regex.Matches(body, "[a-fA-F0-9]{40}").Count > 2,
            "The capture no longer has the bare hashes this rule exists for.");
    }

    /// <remarks>
    /// The title comes off the anchor's own attribute. The text is split across
    /// spans, and joining the nodes glues the words together.
    /// </remarks>
    [Fact]
    public void TorrentGalaxyReadsItsTitleSeedersAndSize()
    {
        SourceRow first = new TorrentGalaxyReader().Read(
            Fixture("torrentgalaxy"),
            new("https://torrentgalaxy.one/get-posts/keywords:Silo%20S03E06/"))[0];

        Assert.Equal("Silo S03E07 1080p HEVC x265-MeGusta EZTV", first.Title);
        Assert.StartsWith(
            "https://torrentgalaxy.one/post-detail/",
            first.DetailUrl?.ToString() ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Equal(9888, first.Seeders);
        Assert.Equal(14421, first.Leechers);
        Assert.Equal((long)(458.4 * 1024 * 1024), first.SizeBytes);
    }

    /// <remarks>
    /// Cut on a spaced dash and nothing else. A scene name is full of dashes
    /// and the one before the release group has no spaces, so cutting on a bare
    /// dash would take the group off every title on the page.
    /// </remarks>
    [Fact]
    public void Torrentz2CutsAForeignSitePrefixAndKeepsTheReleaseGroup()
    {
        IReadOnlyList<SourceRow> rows = new Torrentz2Reader().Read(
            Fixture("torrentz2"),
            new("https://torrentz2.nz/search?q=Silo+S03E06"));

        Assert.NotEmpty(rows);
        Assert.Equal("silo.s03e06.1080p.web.h264-cakes[EZTVx.to].mkv", rows[0].Title);
        Assert.Equal(7595, rows[0].Seeders);
        Assert.Equal(4381, rows[0].Leechers);

        // The prefixed one loses its prefix and keeps everything after it.
        SourceRow prefixed = rows.First(row => row.Title.StartsWith("Silo.S03E06.1080p", StringComparison.Ordinal));
        Assert.Equal("Silo.S03E06.1080p.WEB.H264-CAKES", prefixed.Title);
        Assert.DoesNotContain("UIndex", prefixed.Title, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// The adverts at the top of the page name the search term and look exactly
    /// like results. Every real release has a numeric id in its address and no
    /// advert does.
    /// </remarks>
    [Fact]
    public void TorrentDownloadsSkipsTheAdvertsByTheirMissingNumericId()
    {
        IReadOnlyList<SourceRow> rows = new TorrentDownloadsReader().Read(
            Fixture("torrentdownloads"),
            new("https://www.torrentdownloads.pro/search/?search=Silo+S03E06"));

        Assert.NotEmpty(rows);

        // The adverts are titled "… Torrent" and "… Verified" and point at
        // /td/?search=…, which has no id in it.
        Assert.DoesNotContain(rows, row => row.DetailUrl?.AbsolutePath.StartsWith("/td/", StringComparison.Ordinal) == true);
        Assert.All(rows, row => Assert.Contains("/torrent/", row.DetailUrl?.AbsolutePath ?? string.Empty, StringComparison.Ordinal));

        Assert.Equal("Silo S03E06 MULTI 1080p WEB H264-HiggsBoson exe", rows[0].Title);
        Assert.Equal(2367, rows[0].Seeders);
        Assert.Equal(4219, rows[0].Leechers);
    }

    /// <remarks>
    /// <strong>E1.</strong> Attributes here are bare — <c>class=tv3</c>, not
    /// <c>class="tv3"</c> — and a reader asking for quoted ones reads zero rows
    /// from a page whose own heading says it has results. Thirteen rows in the
    /// capture, and the reader has to find every one of them.
    ///
    /// The page also opens with a block of advertising that names the search
    /// term and links to a third host. None of that is a release.
    /// </remarks>
    [Fact]
    public void TorrentFunkReadsItsBareAttributesAndSkipsTheThirdHostAdverts()
    {
        string body = Fixture("torrentfunk");

        // The two things the rule exists for are really on the page.
        Assert.Contains("class=tv3", body, StringComparison.Ordinal);
        Assert.Contains("t0r.space", body, StringComparison.Ordinal);

        IReadOnlyList<SourceRow> rows = new TorrentFunkReader().Read(
            body,
            new("https://www.torrentfunk.com/all/torrents/silo-s03e06.html"));

        Assert.Equal(13, rows.Count);

        // Not one of them is an advertisement on somebody else's host.
        Assert.All(rows, row => Assert.Equal("www.torrentfunk.com", row.DetailUrl?.Host));

        Assert.Equal("Silo S03E06 The Drive 2160p ATVP WEB-DL ITA ENG DDP5.1 Atmos DV HDR H 265-G66", rows[0].Title);
        Assert.Equal((long)(9.6 * 1024 * 1024 * 1024), rows[0].SizeBytes);
    }

    /// <remarks>
    /// <strong>E2.</strong> The name is split by a span colouring the release
    /// group, so reading the anchor whole and stripping it keeps the group.
    /// Joining its text nodes would run <c>XviD</c> and <c>-AFG</c> together.
    /// </remarks>
    [Fact]
    public void TorrentFunkKeepsTheGroupThatASpanCutsOffTheTitle()
    {
        SourceRow split = new TorrentFunkReader()
            .Read(Fixture("torrentfunk"), new("https://www.torrentfunk.com/all/torrents/silo-s03e06.html"))
            .First(row => row.Title.Contains("XviD", StringComparison.Ordinal));

        Assert.Equal("Silo S03E06 XviD -AFG", split.Title);
        Assert.EndsWith("-AFG", split.Title, StringComparison.Ordinal);
    }

    /// <remarks>
    /// <strong>E6, where the rule actually lives.</strong> A bare forty-hex
    /// string is read as an info hash only when the page has exactly one of
    /// them. Anything else is a coincidence: TorrentGalaxy's page holds seven,
    /// and they are element ids. Taking the first would attach a stranger's
    /// hash to a release, which is worse than having none.
    /// </remarks>
    [Fact]
    public void ABareHashIsReadOnlyWhenThePageHasExactlyOne()
    {
        const string One = "0123456789ABCDEF0123456789ABCDEF01234567";
        const string Another = "89ABCDEF0123456789ABCDEF0123456789ABCDEF";

        Assert.Equal(One, Html.OnlyHash($"<div>{One}</div>"));

        // Case does not make it a different hash, and the answer is upper.
        Assert.Equal(One, Html.OnlyHash($"<div id=\"{One.ToLowerInvariant()}\"></div>"));

        // The same one twice is still one hash.
        Assert.Equal(One, Html.OnlyHash($"<a href=\"{One}\">{One}</a>"));

        // Two different ones are two coincidences, not a hash.
        Assert.Null(Html.OnlyHash($"<div id=\"{One}\"></div><div id=\"{Another}\"></div>"));
        Assert.Null(Html.OnlyHash("<div>nothing hash-shaped here</div>"));
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

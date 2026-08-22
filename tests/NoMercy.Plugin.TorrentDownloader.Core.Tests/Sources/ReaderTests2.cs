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
    /// would be asked for. What that request is, and what the row has to carry
    /// for it to be made, is <c>SignedMagnetTests</c>.
    /// </remarks>
    [Fact]
    public void ATorrentBayRowCarriesTheIdItsMagnetWouldBeAskedFor()
    {
        string body = Fixture("torrentbay");

        IReadOnlyList<SourceRow> rows = new TorrentBayReader()
            .Read(body, new("https://extranet.torrentbay.st/browse/"));

        Assert.Equal("21152668", rows[0].Claim?.TorrentId);
        Assert.All(rows, row => Assert.Null(row.Magnet));

        Assert.Empty(new TorrentBayReader().Read(
            "<tr><td>a row with no button</td></tr>",
            new("https://extranet.torrentbay.st/browse/")));
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

        // The first row of the page is an executable and is no longer a
        // release at all, so the first row here is the one after it — with the
        // file type this site writes after every name taken off.
        Assert.Equal("Silo S03E06 The Drive 720p ATVP WEB-DL DDP5 1 Atmos H 264-playWEB[EZTVx to]", rows[0].Title);
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
    /// <strong>This site writes the file's type as the last word of the
    /// title.</strong> The capture carries
    /// <c>Silo S03E06 MULTI 1080p WEB H264-HiggsBoson exe</c>, and on the
    /// owner's own library on 22 August 2026 the cycle chose
    /// <c>Sugar 2024 S02E08 1080p ATVP WEB-DL DDP5 1 Atmos H 264-FLUX exe</c>
    /// over the release the owner wanted.
    ///
    /// Two things are wrong with that. The word is not part of the release
    /// name, so it is written against the grab and staging matches a finished
    /// file by a name nothing answers to. And <c>exe</c> is not an episode at
    /// all — nor is <c>scr</c>, which is the same thing wearing a screensaver's
    /// extension. A row naming a type this plugin cannot play is not a
    /// candidate for anything, whatever it is called.
    /// </remarks>
    [Fact]
    public void TorrentDownloadsRowsNameTheirTypeAndOnlyVideoIsARelease()
    {
        IReadOnlyList<SourceRow> rows = new TorrentDownloadsReader().Read(
            Fixture("torrentdownloads"),
            new("https://www.torrentdownloads.pro/search/?search=Silo+S03E06"));

        // The type is off the name, and the name is what the release is called.
        Assert.Contains("Silo S03E06 1080p HEVC x265-MeGusta[EZTVx to]", rows.Select(row => row.Title));

        Assert.DoesNotContain(
            rows,
            row => row.Title.EndsWith(" mkv", StringComparison.OrdinalIgnoreCase)
                   || row.Title.EndsWith(" avi", StringComparison.OrdinalIgnoreCase)
                   || row.Title.EndsWith(" mp4", StringComparison.OrdinalIgnoreCase));

        // And the row that is an executable is not a release of anything. Its
        // own page address is what identifies it: the same release is listed a
        // second time on this page, legitimately, without a type after it.
        Assert.DoesNotContain(
            rows,
            row => row.DetailUrl!.AbsolutePath.EndsWith("-exe", StringComparison.OrdinalIgnoreCase));

        // The release group is not a file type and is never taken off. Most
        // rows end in one.
        Assert.Contains("Silo S03E06 1080p x265-ELiTE", rows.Select(row => row.Title));
    }

    /// <remarks>
    /// <strong>A separator the page already had is not a gap to fill.</strong>
    /// This site colours the matched words with spans and writes a scene name
    /// around them:
    /// <c>&lt;span&gt;Silo&lt;/span&gt;.&lt;span&gt;S03E08&lt;/span&gt;.1080p.WEB.H264-CAKES</c>.
    /// A tag worth a space wherever it stood turned that into
    /// <c>Silo . S03E08 .1080p.WEB.H264-CAKES</c> — twenty-six of the
    /// thirty-four rows on the capture of 22 August 2026, including the copy of
    /// the episode the owner's library was missing.
    ///
    /// It matters beyond how it reads. The announced name is what is written
    /// against the grab and what the staging matches a finished file by, so a
    /// name with spaces the release never had is a name nothing answers to.
    /// </remarks>
    [Fact]
    public void ASceneNameCutIntoSpansKeepsItsOwnSeparators()
    {
        IReadOnlyList<SourceRow> rows = new TorrentBayReader().Read(
            Fixture("torrentbay-scene-names"),
            new("https://extranet.torrentbay.st/browse/?q=Silo+S03E08"));

        Assert.Contains("Silo.S03E08.1080p.WEB.H264-CAKES", rows.Select(row => row.Title));

        // A space before a separator is always the stripper's: no release name
        // on any capture has one. A space after a dot is not — one row on this
        // page really is called "…Atmos. X265 POOTLED…" — so only the first is
        // worth asserting, and asserting the second refused a real name.
        Assert.DoesNotContain(rows, row => row.Title.Contains(" .", StringComparison.Ordinal));
    }

    /// <remarks>
    /// <strong>E2.</strong> The name is split by a span colouring the release
    /// group, so reading the anchor whole and stripping it keeps the group —
    /// reading only the first text node would lose it.
    ///
    /// This asserted <c>Silo S03E06 XviD -AFG</c>, with a space nothing on the
    /// page put there, for as long as a tag was worth a space wherever it
    /// stood. The release is called <c>XviD-AFG</c> and the space was the
    /// stripper's, not the site's.
    /// </remarks>
    [Fact]
    public void TorrentFunkKeepsTheGroupThatASpanCutsOffTheTitle()
    {
        SourceRow split = new TorrentFunkReader()
            .Read(Fixture("torrentfunk"), new("https://www.torrentfunk.com/all/torrents/silo-s03e06.html"))
            .First(row => row.Title.Contains("XviD", StringComparison.Ordinal));

        Assert.Equal("Silo S03E06 XviD-AFG", split.Title);
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

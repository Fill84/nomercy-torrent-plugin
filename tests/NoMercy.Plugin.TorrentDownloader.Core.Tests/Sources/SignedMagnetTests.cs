using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Sources;

/// <summary>
/// Asking TorrentBay for a torrent it will not print.
/// </summary>
/// <remarks>
/// Every value here is off the real capture in <c>tests/fixtures</c> and the
/// script that page loads. This is the one shipped source that names its
/// torrents nowhere at all — not on the listing, not on the row's own page —
/// and it was left unwritten from <c>S2-06</c> to <c>S6-01</c> and then
/// forgotten. It publishes honest seeder counts and sorts by them, so its rows
/// outranked every other site's, were chosen, were followed, named no torrent,
/// and the episode was reported as though nobody were serving it.
/// </remarks>
public class SignedMagnetTests
{
    /// <summary>The page token the captured search page declared for itself.</summary>
    private const string PageToken = "a6a622df2ec4db6af7de1133837ee5bf";

    /// <summary>The session that page was served to.</summary>
    private const string SessionId = "0c01634dba9aa280bc08db6088889c8a";

    /// <summary>The id on the first row's own magnet button.</summary>
    private const string TorrentId = "21152668";

    /// <remarks>
    /// The reader carries the row's id and the page's two tokens together,
    /// because the tokens belong to the page the row came from — a token from
    /// another page is refused, and a row that arrived without one cannot be
    /// asked at all.
    /// </remarks>
    [Fact]
    public void TheListingCarriesWhatTheSiteHasToBeAskedForEachRow()
    {
        IReadOnlyList<SourceRow> rows = new TorrentBayReader().Read(
            Fixture("torrentbay"),
            new("https://extranet.torrentbay.st/browse/?q=Silo+S03E06"));

        SourceRow first = rows[0];

        Assert.Equal("Silo S03E06 1080p WEB H264-CAKES EZTV", first.Title);

        // Neither of the two things every other site gives, which is the whole
        // reason this exists.
        Assert.Null(first.Magnet);
        Assert.Null(first.InfoHash);

        SignedClaim claim = Assert.IsType<SignedClaim>(first.Claim);

        Assert.Equal(TorrentId, claim.TorrentId);
        Assert.Equal(PageToken, claim.PageToken);
        Assert.Equal(SessionId, claim.SessionId);

        // And every row of the page, not only the first: the tokens are read
        // once for the page and each row brings its own id.
        Assert.All(rows, row => Assert.NotNull(row.Claim));
        Assert.Equal(rows.Count, rows.Select(row => row.Claim!.TorrentId).Distinct().Count());
    }

    /// <remarks>
    /// A page that declares no token cannot be asked, and says so by carrying
    /// no claim rather than half of one. Half a claim posts a request the site
    /// refuses, and a refusal from this site reads exactly like a site with
    /// nothing to offer.
    /// </remarks>
    [Fact]
    public void ARowFromAPageWithNoTokenCarriesNoClaimAtAll()
    {
        string withoutToken = Fixture("torrentbay")
            .Replace($"window.searchPageToken = '{PageToken}';", string.Empty, StringComparison.Ordinal);

        IReadOnlyList<SourceRow> rows = new TorrentBayReader().Read(
            withoutToken,
            new("https://extranet.torrentbay.st/browse/?q=Silo+S03E06"));

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Null(row.Claim));
    }

    /// <remarks>
    /// The signature is SHA-256 over the id, the moment and the page's token
    /// joined by bars, which is what the site's own script computes. Asserted
    /// against a digest worked out independently of this code: a test that
    /// calls the same method it is testing agrees with itself and with nobody
    /// else.
    /// </remarks>
    [Fact]
    public void TheSignatureIsOverTheIdTheMomentAndThePagesToken()
    {
        Assert.Equal(
            "769790eab90e81defee729c7a5faad678986b810a7d86db0da3505f03a9c337a",
            SignedMagnet.Signature(TorrentId, 1755835200L, PageToken));
    }

    /// <remarks>
    /// The body carries the six fields the site's script posts, in a form it
    /// sends. <c>hash</c> and <c>name</c> go empty because the button carries
    /// neither and the script posts them anyway — a request shaped differently
    /// from the site's own is one nobody has seen it accept.
    /// </remarks>
    [Fact]
    public void TheBodyIsTheSixFieldsTheSitesOwnScriptPosts()
    {
        string body = SignedMagnet.Body(
            new(TorrentId, PageToken, SessionId),
            DateTimeOffset.FromUnixTimeSeconds(1755835200L));

        Assert.Equal(
            "torrent_id=21152668"
            + "&hash="
            + "&name="
            + "&timestamp=1755835200"
            + "&hmac=769790eab90e81defee729c7a5faad678986b810a7d86db0da3505f03a9c337a"
            + $"&sessid={SessionId}",
            body);
    }

    /// <remarks>
    /// Where it posts is worked out from the row's own address rather than
    /// written down. The site moves between hosts, and the clearance that lets
    /// this request through belongs to the host the page came from.
    /// </remarks>
    [Fact]
    public void ItPostsToTheHostTheRowCameFrom()
    {
        Assert.Equal(
            "https://extranet.torrentbay.st/ajax/getSearchMagnet.php",
            SignedMagnet.EndpointOn(
                new("https://extranet.torrentbay.st/silo-s03e08-x265-neonoir-21446438/")).ToString());
    }

    /// <remarks>
    /// A refusal is not a magnet. The caller has another copy to try and only
    /// knows to try it if this says no — and this site answers a refusal with
    /// the same shape of body as a success, which is how a "no" comes to be
    /// read as an address.
    /// </remarks>
    [Theory]
    [InlineData("""{"success":true,"url":"magnet:?xt=urn:btih:ABC123"}""", "magnet:?xt=urn:btih:ABC123")]
    [InlineData("""{"success":false,"error":"Invalid or expired token."}""", null)]
    [InlineData("""{"success":true}""", null)]
    [InlineData("""{"success":true,"url":"https://example.test/not-a-magnet"}""", null)]
    [InlineData("<html><body>a challenge, not an answer</body></html>", null)]
    [InlineData("", null)]
    public void OnlyASuccessCarryingAMagnetIsRead(string answered, string? expected)
    {
        Assert.Equal(expected, SignedMagnet.MagnetIn(answered));
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

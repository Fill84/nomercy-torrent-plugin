using System.Net;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// Telling the interstitial from the page behind it, against both of them.
/// </summary>
/// <remarks>
/// <para>
/// This class had no test at all, and its own remark said why that mattered:
/// "These are not pinned by a captured page yet — there is no capture of a
/// challenge in tests/fixtures/." A detector nobody can prove wrong is one that
/// stays wrong, and this one was.
/// </para>
/// <para>
/// Both captures are real, taken from 1337x on 10 September 2026: the
/// interstitial as it is served to a plain request, and the search page itself
/// as it is served to the browser five seconds later, with the clearance cookie
/// earned and Cloudflare's detections script still on it.
/// </para>
/// </remarks>
public class CloudflareChallengeTests
{
    /// <remarks>
    /// <para>
    /// <strong>The page that cost two indexers everything.</strong> Cloudflare
    /// leaves <c>/cdn-cgi/challenge-platform/scripts/jsd/main.js</c> on an
    /// ordinary successful response — it is the JavaScript-detections script,
    /// not a challenge. <c>challenge-platform</c> was in the marker list, so
    /// this page, 654 KB of search results with nine torrent rows on it, was
    /// read as "still a challenge".
    /// </para>
    /// <para>
    /// The solver polls until this says false. It never did, so it waited out
    /// its forty-five seconds and reported that the browser could not get past
    /// a challenge that had cleared in five. 1337x — priority 40, the second
    /// highest in the catalogue — and EZTV both answered nothing for at least
    /// ten days, and the health tool called it a challenge every time.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePageItLeavesItsDetectionsScriptOnIsNotAChallenge()
    {
        string cleared = Fixture("cloudflare-cleared-1337x.html");

        // The capture really is the page and really does carry the script, or
        // this test would pass for the wrong reason.
        Assert.Contains("Download South Park S15E12 Torrents", cleared, StringComparison.Ordinal);
        Assert.Contains("challenge-platform", cleared, StringComparison.Ordinal);

        Assert.False(CloudflareChallenge.IsChallengePage(cleared));
    }

    /// <remarks>
    /// The other half, and the one that must not be given up to get the first:
    /// the interstitial is still a challenge. It carries <c>cf_chl_opt</c> seven
    /// times and a "Just a moment" title, and neither appears on the page
    /// behind it.
    /// </remarks>
    [Fact]
    public void TheInterstitialIsStillAChallenge()
    {
        Assert.True(CloudflareChallenge.IsChallengePage(Fixture("cloudflare-interstitial-1337x.html")));
    }

    /// <remarks>
    /// Over HTTP there is a response to read, and the header is a statement
    /// rather than a guess. The same interstitial, with the status and the
    /// header 1337x really answered.
    /// </remarks>
    [Fact]
    public void TheInterstitialOverHttpIsAChallengeByItsHeader()
    {
        using HttpResponseMessage response = new(HttpStatusCode.Forbidden);
        response.Headers.TryAddWithoutValidation(CloudflareChallenge.MitigatedHeader, "challenge");

        Assert.True(CloudflareChallenge.IsChallenge(response, Fixture("cloudflare-interstitial-1337x.html")));
    }

    /// <remarks>
    /// And a page served with no mitigation header is not a challenge, whatever
    /// scripts happen to be on it. Two hundred with the detections script on it
    /// is the ordinary case for every Cloudflare site this plugin reads.
    /// </remarks>
    [Fact]
    public void ASuccessfulResponseCarryingThatScriptIsNotAChallenge()
    {
        using HttpResponseMessage response = new(HttpStatusCode.OK);

        Assert.False(CloudflareChallenge.IsChallenge(response, Fixture("cloudflare-cleared-1337x.html")));
    }

    private static string Fixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine(directory!.FullName, "tests", "fixtures", name));
    }
}

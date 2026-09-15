using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>TorrentBay's torrent, asked for the way the chain really asks.</summary>
public sealed class TheSignedRequestTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nomercy-signed-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// <para>
    /// The chain hands the find the fetch to post with, not the browser. The browser posted from a fresh tab
    /// on no page of the site, and on 15 September 2026 that was found failing for every TorrentBay row
    /// since 30 August — while TorrentBay is a first-choice indexer.
    /// </para>
    /// <para>
    /// A row read off the captured listing, its hash asked for through the chain: the signed request reaches
    /// the wire over HTTP, to the row's own host, and the magnet it answers is the row's hash.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TorrentBayIsAskedForItsTorrentOverHttpWithNoBrowser()
    {
        FakeHttp http = new();
        http.Answers(System.Net.HttpStatusCode.OK, "{\"success\":true,\"url\":\"magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567&tr=udp%3A%2F%2Ft.test%3A80%2Fannounce\"}");

        FakeGrants grants = new();
        grants.Grant("extranet.torrentbay.st");

        FakePluginContext context = new() { DataFolderPath = _folder, Permits = grants };

        await using Chain chain = new(context, new ActivityJournal(), new CatalogueLoader(new CapturingLogger()).Load(), http);

        Uri listing = new("https://extranet.torrentbay.st/browse/?q=Silo.S02E01.1080p.WEB.H264-SuccessfulCrab&sort=seeders&order=desc");
        SourceRow row = Readers.Shipped().Named("torrentbay")!
            .Read(Fixture("round-silo-torrentbay-exact.html"), listing)
            .First(one => one.Claim is not null);

        ReleaseCopy read = await chain.Find(new Settings()).HashOfAsync(
            new ReleaseCopy(row.Title, "TorrentBay", 60, null, null, row.DetailUrl) { Claim = row.Claim },
            CancellationToken.None);

        Assert.Equal("0123456789ABCDEF0123456789ABCDEF01234567", read.InfoHash);
        Assert.Contains("udp://t.test:80/announce", read.Trackers);

        HttpRequestMessage sent = Assert.Single(http.Attempts);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://extranet.torrentbay.st/ajax/getSearchMagnet.php", sent.RequestUri!.ToString());
    }

    public void Dispose()
    {
        TemporaryFolder.Forget(_folder);
    }

    private static string Fixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine(directory!.FullName, "tests", "fixtures", name));
    }
}

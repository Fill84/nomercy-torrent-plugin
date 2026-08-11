// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Indexers;

/// <summary>
/// A site the owner names, read without knowing the site.
/// </summary>
public class SiteIndexerTests
{
    private const string Row =
        """
        <tr><td><a href="magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&amp;dn=South.Park.S29E02.1080p.WEB.h264-GROUP&amp;tr=udp://t.test">
        South Park S29E02</a></td><td>2.1 GB</td><td>Seeders: 148</td><td>3</td></tr>
        """;

    // The magnet carries the release name in its own dn, which is the row's identity and
    // its payload in one string - no knowledge of the markup around it needed.
    [Fact]
    public void Parse_ReadsTheReleaseNameOutOfTheMagnetItself()
    {
        SiteRow row = SiteListingParser.Parse(Row, []).Should().ContainSingle().Subject;

        row.Title.Should().Be("South.Park.S29E02.1080p.WEB.h264-GROUP");
        row.InfoHash.Should().Be("0123456789abcdef0123456789abcdef01234567");
        row.Seeders.Should().Be(148);
    }

    [Fact]
    public void Parse_ReadsSeedersWrittenTheOtherWayRound()
    {
        string html = Row.Replace("Seeders: 148", "148 seeders");

        SiteListingParser.Parse(html, []).Should().ContainSingle().Which.Seeders.Should().Be(148);
    }

    // Zero is honest. The profile's minimum-seeders rule then refuses the row, which is
    // the right outcome for a listing that cannot be trusted to say.
    [Fact]
    public void Parse_LeavesSeedersAtZeroWhenThePageDoesNotSayIt()
    {
        string html = Row.Replace("Seeders: 148", "").Replace("<td>3</td>", "");

        SiteListingParser.Parse(html, []).Should().ContainSingle().Which.Seeders.Should().Be(0);
    }

    // A listing and its details panel are one torrent, not two.
    [Fact]
    public void Parse_CountsARepeatedMagnetOnce()
    {
        SiteListingParser.Parse(Row + Row, []).Should().ContainSingle();
    }

    // A torrent nobody can name cannot be matched to an episode, so it would download and
    // then have no library to belong to.
    [Fact]
    public void Parse_SkipsAMagnetWithNoNameInIt()
    {
        SiteListingParser.Parse("<a href=\"magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567\">x</a>", [])
            .Should().BeEmpty();
    }

    [Fact]
    public void Parse_FindsAMagnetThatIsNotInAnHref()
    {
        string html = "<button data-magnet='magnet:?xt=urn:btih:abc&dn=Some.Show.S01E01.1080p'>get</button>";

        SiteListingParser.Parse(html, []).Should().ContainSingle().Which.Title.Should().Be("Some.Show.S01E01.1080p");
    }

    [Fact]
    public void Parse_SurvivesAPageWithNothingInIt()
    {
        SiteListingParser.Parse("<html><body>no results</body></html>", []).Should().BeEmpty();
        SiteListingParser.Parse("", []).Should().BeEmpty();
    }

    // The owner reads the template off their own address bar, so it has to be checked when
    // they save it rather than failing silently on every later search.
    [Theory]
    [InlineData("https://site.test/search/{query}/", true)]
    [InlineData("https://site.test/?q={query}&sort=seeds", true)]
    [InlineData("https://site.test/search/", false)]
    [InlineData("not a url {query}", false)]
    [InlineData("", false)]
    public void IsUsableTemplate_AcceptsOnlyAnAbsoluteUrlWithAPlaceholder(string template, bool usable)
    {
        SiteIndexer.IsUsableTemplate(template).Should().Be(usable);
    }

    // A site search matches on the whole string, and the show alone returns a decade of it.
    [Fact]
    public async Task SearchAsync_AsksForTheShowAndTheSlotTogether()
    {
        RecordingFetch fetch = new(Row);
        SiteIndexer indexer = new("site-a", 30, "https://site.test/search/{query}/", fetch.Fetch(), []);

        IReadOnlyList<ReleaseInfo> found = await indexer.SearchAsync(
            new SearchQuery("South Park", new EpisodeSlot(29, 2)),
            CancellationToken.None);

        // AbsoluteUri, not ToString: Uri.ToString unescapes a path for display, so the
        // escaping this asserts would be invisible there while still being on the wire.
        fetch.LastUrl!.AbsoluteUri.Should().Contain("South%20Park%20S29E02");
        found.Should().ContainSingle().Which.MagnetUri.Should().StartWith("magnet:");
    }

    // Torznab and RSS both answer an empty query usefully. A search page answers it with
    // its front page, or with everything it has, and neither is what the feed wants.
    [Fact]
    public async Task SearchAsync_RefusesToAskASiteForEverything()
    {
        RecordingFetch fetch = new(Row);
        SiteIndexer indexer = new("site-a", 30, "https://site.test/search/{query}/", fetch.Fetch(), []);

        (await indexer.SearchAsync(new SearchQuery(""), CancellationToken.None)).Should().BeEmpty();
        fetch.LastUrl.Should().BeNull("nothing was worth asking");
    }

    private sealed class RecordingFetch(string body)
    {
        public Uri? LastUrl { get; private set; }

        public ChallengeAwareFetch Fetch() =>
            new(new HttpClient(new Handler(this, body)), new ClearanceStore(() => DateTimeOffset.UnixEpoch));

        private sealed class Handler(RecordingFetch owner, string body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                owner.LastUrl = request.RequestUri;

                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(body),
                });
            }
        }
    }
}

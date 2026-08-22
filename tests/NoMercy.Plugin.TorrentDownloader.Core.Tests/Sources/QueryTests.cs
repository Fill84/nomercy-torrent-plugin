using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Sources;

public class QueryTests
{
    private const string Release = "Silo.S03E06.1080p.WEB.H264-CAKES";

    /// <remarks>
    /// <strong>E3.</strong> A plus in a path is a plus. TorrentGalaxy answered
    /// nothing for a release it has sixteen copies of because the term arrived
    /// with pluses where it wanted spaces — and 1337x searches from its path too
    /// and wants the opposite. Both are asserted on the address itself.
    /// </remarks>
    [Fact]
    public void SpacedSendsPerCentTwentyAndTheDefaultSendsAPlus()
    {
        Assert.Equal(
            "https://torrentgalaxy.one/get-posts/keywords:Silo%20S03E06%201080p%20WEB%20H264%20CAKES/",
            Query.Write("https://torrentgalaxy.one/get-posts/keywords:{query}/", Release, QueryStyles.Spaced));

        Assert.Equal(
            "https://www.1337x.to/sort-category-search/Silo+S03E06+1080p+WEB+H264+CAKES/TV/time/desc/1/",
            Query.Write("https://www.1337x.to/sort-category-search/{query}/TV/time/desc/1/", Release, QueryStyles.Words));
    }

    /// <remarks>
    /// A release name is full of dots and dashes that mean nothing to a search
    /// box. Sent verbatim, a site answers nothing at all.
    /// </remarks>
    [Fact]
    public void PunctuationBecomesWordBoundaries()
    {
        Assert.Equal("Silo+S03E06+1080p+WEB+H264+CAKES", Query.Format(Release, QueryStyles.Words));
    }

    /// <remarks>
    /// A site whose search is the path segment: one dash between words, runs
    /// collapsed, lowercase. srrDB's search is its own path and wants exactly
    /// this shape.
    /// </remarks>
    [Fact]
    public void SlugIsLowercaseAndSingleDashed()
    {
        Assert.Equal("silo-s03e06-1080p-web-h264-cakes", Query.Format(Release, QueryStyles.Slug));
        Assert.Equal("silo-s03e06", Query.Format("Silo -- S03E06", QueryStyles.Slug));
    }

    /// <remarks>
    /// Verbatim is for an endpoint matching a string rather than tokenising it,
    /// so the term survives — escaped, because it still has to be an address.
    /// </remarks>
    [Fact]
    public void VerbatimKeepsTheTermAndEscapesIt()
    {
        Assert.Equal("Silo.S03E06.1080p.WEB.H264-CAKES", Query.Format(Release, QueryStyles.Verbatim));
        Assert.Equal("Silo%20S03E06", Query.Format("Silo S03E06", QueryStyles.Verbatim));
    }

    /// <remarks>
    /// Every shipped source's own style, on its own address, so a style that
    /// stops matching its site is caught here rather than by an empty page.
    /// </remarks>
    [Fact]
    public void APlaceholderAnywhereInTheAddressIsFilled()
    {
        Assert.Equal(
            "https://predb.me/?search=Silo+S03E06&rss=1",
            Query.Write("https://predb.me/?search={query}&rss=1", "Silo S03E06", QueryStyles.Words));
    }
}

using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>The release names that passed, grouped by how many wishes each carries.</summary>
/// <remarks><c>docs/specs/release-names.md</c> § Choosing the release name.</remarks>
public sealed class WishGroupsTests
{
    private const string AtmosPlayWeb = "Silo S03E06 The Drive 720p ATVP WEB-DL DDP5 1 Atmos H 264-playWEB";
    private const string Ntb = "Silo S03E06 The Drive 1080p ATVP WEB-DL DDP5 1 H 264-NTb";
    private const string Cakes = "Silo S03E06 1080p WEB H264-CAKES";
    private const string Elite = "Silo.S03E06.1080p.x265-ELiTE";

    /// <remarks>
    /// The names carrying the most wishes are searched first, and names carrying the same number of wishes
    /// together. Two carry two of the three wishes, one carries one, and one carries none.
    /// </remarks>
    [Fact]
    public void NamesAreGroupedByHowManyWishesTheyCarryMostFirst()
    {
        // Each a name a site really published.
        Assert.Contains(Cakes, Capture.Rows("eztv.html", "eztv"));
        Assert.Contains(Elite, Capture.Rows("1337x.html", "1337x"));
        Assert.Contains(Ntb, Capture.Rows("limetorrents.html", "site"));
        Assert.Contains(AtmosPlayWeb, Capture.Rows("limetorrents.html", "site"));

        IReadOnlyList<IReadOnlyList<string>> groups = WishGroups.Of([Cakes, Elite, Ntb, AtmosPlayWeb], ["NTb", "Atmos", "WEB"]);

        Assert.Equal(3, groups.Count);
        Assert.Equal([Ntb, AtmosPlayWeb], groups[0]);
        Assert.Equal([Cakes], groups[1]);
        Assert.Equal([Elite], groups[2]);
    }

    /// <remarks>
    /// A wish that no release name carries leaves the show downloadable: the names without it are taken
    /// instead, all of them together as the one group there is.
    /// </remarks>
    [Fact]
    public void AWishNoNameCarriesLeavesTheShowDownloadable()
    {
        IReadOnlyList<IReadOnlyList<string>> groups = WishGroups.Of([Cakes, Elite], ["DUAL"]);

        Assert.Equal([Cakes, Elite], Assert.Single(groups));
    }

    /// <remarks>And a show with no wishes at all has its names in one group, in the order they came.</remarks>
    [Fact]
    public void NoWishesIsOneGroup()
    {
        Assert.Equal([Elite, Cakes], Assert.Single(WishGroups.Of([Elite, Cakes], [])));
        Assert.Empty(WishGroups.Of([], ["DUAL"]));
    }
}

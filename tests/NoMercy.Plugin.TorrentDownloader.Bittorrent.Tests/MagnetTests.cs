using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// Magnet links, from one a site really published.
/// </summary>
/// <remarks>
/// The magnet below is the whole of what LimeTorrents prints on the detail page
/// captured in <c>tests/fixtures/limetorrents-detail.html</c>: forty hex
/// characters, a display name, and six trackers.
/// </remarks>
public class MagnetTests
{
    /// <remarks>
    /// The hash, the name and every tracker. A magnet with its trackers dropped
    /// still downloads — from the DHT, slowly — which is exactly the kind of
    /// fault nobody reports and everybody feels.
    /// </remarks>
    [Fact]
    public void ARealMagnetYieldsItsHashItsNameAndEveryTracker()
    {
        Magnet magnet = Assert.IsType<Magnet>(Magnet.Parse(Real));

        Assert.Equal("92D8A3F6864911EF292B4BE0DD5286406396D2B3", magnet.InfoHash);
        Assert.Equal("Silo S03E06 1080p WEB H264-CAKES", magnet.DisplayName);

        Assert.Equal(6, magnet.Trackers.Count);
        Assert.Equal("udp://open.stealth.si:80/announce", magnet.Trackers[0]);
        Assert.Equal("udp://tracker.opentrackr.org:1337/announce", magnet.Trackers[1]);
    }

    /// <remarks>
    /// The other spelling of the same hash. BEP 9 allows base32 and plenty of
    /// sites use it; this is the hash above, encoded the other way by a second
    /// implementation, and it has to come out as the same forty characters or
    /// the same torrent would be taken twice.
    /// </remarks>
    [Fact]
    public void ABase32HashIsTheSameHash()
    {
        Magnet magnet = Assert.IsType<Magnet>(
            Magnet.Parse("magnet:?xt=urn:btih:SLMKH5UGJEI66KJLJPQN2UUGIBRZNUVT&dn=Silo"));

        Assert.Equal("92D8A3F6864911EF292B4BE0DD5286406396D2B3", magnet.InfoHash);
    }

    /// <remarks>
    /// Case is not part of a hash, and a site that prints it in lower case is
    /// offering the same torrent as the one that prints it in upper.
    /// </remarks>
    [Fact]
    public void TheHashIsReadWhicheverCaseItIsWrittenIn()
    {
        Assert.Equal(
            "92D8A3F6864911EF292B4BE0DD5286406396D2B3",
            Magnet.Parse("magnet:?xt=urn:btih:92d8a3f6864911ef292b4be0dd5286406396d2b3")!.InfoHash);
    }

    /// <remarks>
    /// Anything that is not a magnet with a usable hash in it is refused rather
    /// than half-read. A torrent handed to the client under a hash that is
    /// forty characters of something else is one that never finds a peer and
    /// never says why.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("https://example.test/not-a-magnet")]
    [InlineData("magnet:?dn=No+hash+at+all")]
    [InlineData("magnet:?xt=urn:btih:tooshort")]
    [InlineData("magnet:?xt=urn:btih:NOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOTH")]
    [InlineData("magnet:?xt=urn:sha1:92D8A3F6864911EF292B4BE0DD5286406396D2B3")]
    public void AnythingThatIsNotAMagnetWithAHashIsRefused(string text)
    {
        Assert.Null(Magnet.Parse(text));
    }

    /// <remarks>
    /// A magnet with no name is still a magnet: the name is a convenience for
    /// people and the hash is what the protocol runs on. It comes back as null
    /// rather than as an empty string, so nothing renders a blank line where a
    /// title should be.
    /// </remarks>
    [Fact]
    public void AMagnetWithNoNameIsStillAMagnet()
    {
        Magnet magnet = Assert.IsType<Magnet>(
            Magnet.Parse("magnet:?xt=urn:btih:92D8A3F6864911EF292B4BE0DD5286406396D2B3"));

        Assert.Null(magnet.DisplayName);
        Assert.Empty(magnet.Trackers);
    }

    /// <summary>What LimeTorrents really printed, trackers and all.</summary>
    private const string Real =
        "magnet:?xt=urn:btih:92D8A3F6864911EF292B4BE0DD5286406396D2B3"
        + "&dn=Silo+S03E06+1080p+WEB+H264-CAKES"
        + "&tr=udp%3A%2F%2Fopen.stealth.si%3A80%2Fannounce"
        + "&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337%2Fannounce"
        + "&tr=udp%3A%2F%2Fexodus.desync.com%3A6969%2Fannounce"
        + "&tr=udp%3A%2F%2Ftracker.torrent.eu.org%3A451%2Fannounce"
        + "&tr=udp%3A%2F%2Ftracker.moeking.me%3A6969%2Fannounce"
        + "&tr=udp%3A%2F%2Fopentracker.i2p.rocks%3A6969%2Fannounce";
}

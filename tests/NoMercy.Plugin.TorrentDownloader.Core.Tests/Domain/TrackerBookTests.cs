using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Domain;

/// <summary>
/// Every tracker this plugin comes across, kept and reused.
/// </summary>
/// <remarks>
/// The owner's decision, 20 August 2026: the default list is not something
/// somebody types in, it is everything the plugin meets — on a magnet, on a
/// listing, on a torrent it is holding — with no duplicates, and it travels
/// with every grab afterwards. More trackers is a faster download, and the
/// swarm a release was posted to is usually the swarm the next one is in.
/// </remarks>
public class TrackerBookTests
{
    [Fact]
    public void EveryTrackerComeAcrossIsKept()
    {
        IReadOnlyList<string> known = TrackerBook.Learn(
            [],
            ["udp://tracker.one.example:1337/announce", "http://tracker.two.example/announce"],
            []);

        Assert.Equal(
            ["udp://tracker.one.example:1337/announce", "http://tracker.two.example/announce"],
            known);
    }

    /// <remarks>
    /// No duplicates, however it is spelled. The same tracker announced twice
    /// per torrent is one that bans this client for hammering it, and a list
    /// that grew every cycle would do exactly that within a week.
    /// </remarks>
    [Fact]
    public void OneAlreadyKnownIsNotKeptTwiceHoweverItIsSpelled()
    {
        IReadOnlyList<string> known = TrackerBook.Learn(
            ["udp://tracker.one.example:1337/announce"],
            ["UDP://Tracker.One.Example:1337/announce", "udp://tracker.one.example:1337/announce"],
            []);

        Assert.Single(known);
    }

    /// <remarks>
    /// In the order they were first met, and the ones already known stay where
    /// they are. The list is written into the owner's settings on every cycle,
    /// and one that reordered itself would rewrite the file for ever with
    /// nothing having changed.
    /// </remarks>
    [Fact]
    public void TheOnesAlreadyKnownKeepTheirPlace()
    {
        IReadOnlyList<string> known = TrackerBook.Learn(
            ["udp://first.example/announce", "udp://second.example/announce"],
            ["udp://second.example/announce", "udp://third.example/announce"],
            []);

        Assert.Equal(
            ["udp://first.example/announce", "udp://second.example/announce", "udp://third.example/announce"],
            known);
    }

    /// <remarks>
    /// <para>
    /// <strong>A passkey is never kept.</strong> A private tracker's announce
    /// address carries the owner's own key in it, and this list is attached to
    /// every grab — so learning one would hand the owner's credentials to every
    /// public swarm they download from, and print them in the settings.
    /// </para>
    /// <para>
    /// A query string is where a passkey lives, and no public tracker needs
    /// one. Refusing the shape rather than looking for the word is what makes
    /// this hold for a key nobody has thought of yet.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("https://private.example/announce?passkey=a1b2c3d4e5f6")]
    [InlineData("https://private.example/announce?pk=a1b2c3d4e5f6")]
    [InlineData("https://a1b2c3d4e5f6@private.example/announce")]
    public void AnAddressCarryingASecretIsNeverKept(string announce)
    {
        Assert.Empty(TrackerBook.Learn([], [announce], []));
    }

    /// <remarks>
    /// And neither is anything on a host the owner set up as a private tracker,
    /// whatever the address looks like. Their own tracker is theirs: it belongs
    /// to the torrents it issued and to nothing else.
    /// </remarks>
    [Fact]
    public void NothingOnTheOwnersOwnPrivateTrackerIsEverKept()
    {
        Assert.Empty(TrackerBook.Learn(
            [],
            ["https://tracker.private.example/announce"],
            ["tracker.private.example"]));
    }

    /// <remarks>
    /// Only something that could be announced to. A magnet's tracker field
    /// carries whatever was written into it, and a list with rubbish in it is
    /// one that spends part of every announce round on addresses that cannot
    /// answer.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url at all")]
    [InlineData("magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("file:///etc/passwd")]
    public void SomethingThatIsNotATrackerIsNotKept(string rubbish)
    {
        Assert.Empty(TrackerBook.Learn([], [rubbish], []));
    }
}

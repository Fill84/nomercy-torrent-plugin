using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// Choosing one copy, or none.
/// </summary>
/// <remarks>
/// <strong>H1.</strong> The real profile, never a stand-in chooser. Every test
/// covering 0.3.4's seeder fault stubbed the profile out and passed while the
/// plugin took nothing at all for a fortnight.
/// </remarks>
public class ReleaseDeciderTests
{
    /// <remarks>
    /// Seeders first. Between two copies of the same release, the one more
    /// people are serving is the one that arrives.
    /// </remarks>
    [Fact]
    public void TheCopyWithTheMostSeedersIsTaken()
    {
        Decision decision = new ReleaseDecider(new() { MinimumSeeders = 2 }).Decide(
            [Copy("LimeTorrents", priority: 35, seeders: 4), Copy("The Pirate Bay", priority: 45, seeders: 40)],
            Blacklist.None);

        Assert.Equal("The Pirate Bay", decision.Chosen!.Source);
    }

    /// <remarks>
    /// Then the site the owner rates higher — <strong>descending</strong>.
    /// 0.3.4 had this inverted and picked the worst-rated site every time two
    /// copies were level, which is most of the time.
    /// </remarks>
    [Fact]
    public void LevelOnSeedersTheHigherRatedSiteWins()
    {
        Decision decision = new ReleaseDecider(new() { MinimumSeeders = 2 }).Decide(
            [Copy("LimeTorrents", priority: 35, seeders: 40), Copy("The Pirate Bay", priority: 45, seeders: 40)],
            Blacklist.None);

        Assert.Equal("The Pirate Bay", decision.Chosen!.Source);
    }

    /// <remarks>
    /// A copy the profile refuses is not chosen, and the reason is kept: the
    /// Skipped page exists to say why, and "nothing worth taking" is the
    /// sentence that hid a whole release's worth of faults.
    /// </remarks>
    [Fact]
    public void ARefusedCopyIsNotChosenAndItsReasonIsKept()
    {
        Decision decision = new ReleaseDecider(new() { MinimumSeeders = 10 }).Decide(
            [Copy("LimeTorrents", priority: 35, seeders: 1), Copy("The Pirate Bay", priority: 45, seeders: 40)],
            Blacklist.None);

        Assert.Equal("The Pirate Bay", decision.Chosen!.Source);

        (ReleaseCopy Copy, string Reason) refused = Assert.Single(decision.Refused);
        Assert.Equal("LimeTorrents", refused.Copy.Source);
        Assert.Contains("LimeTorrents", refused.Reason, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Nothing acceptable is nothing chosen, with every refusal kept. An empty
    /// answer with no reasons behind it is the one the owner cannot act on.
    /// </remarks>
    [Fact]
    public void WhenNoCopyIsAcceptableNoneIsChosenAndEveryReasonIsKept()
    {
        Decision decision = new ReleaseDecider(new() { MinimumSeeders = 10 }).Decide(
            [Copy("LimeTorrents", priority: 35, seeders: 1), Copy("The Pirate Bay", priority: 45, seeders: 2)],
            Blacklist.None);

        Assert.Null(decision.Chosen);
        Assert.Equal(2, decision.Refused.Count);
    }

    /// <remarks>
    /// Asked about nothing, it chooses nothing and says nothing was refused.
    /// An episode no indexer answered for is not an episode whose copies were
    /// all rejected, and the two read very differently on a page.
    /// </remarks>
    [Fact]
    public void NoCopiesAtAllIsNotTheSameAsEveryCopyRefused()
    {
        Decision decision = new ReleaseDecider(new()).Decide([], Blacklist.None);

        Assert.Null(decision.Chosen);
        Assert.Empty(decision.Refused);
    }

    private static ReleaseCopy Copy(string source, int priority, int seeders)
    {
        return new("Silo.S03E06.1080p.WEB.H264-CAKES", source, priority, null, null, null, seeders, null);
    }
}

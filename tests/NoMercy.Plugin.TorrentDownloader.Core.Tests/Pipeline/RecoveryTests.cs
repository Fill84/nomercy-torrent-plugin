using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// Making the client and the store agree again after a restart.
/// </summary>
/// <remarks>
/// They drift apart for ordinary reasons — the server was killed mid-write, a
/// download finished while it was down, a database was restored from yesterday
/// — and docs/06-torrent-client.md § Recovery gives one answer for each case.
/// This is that table, put to every row of it.
/// </remarks>
public class RecoveryTests
{
    /// <remarks>
    /// In both and still running: nothing to do. A plan that re-added it would
    /// have two clients writing the same file.
    /// </remarks>
    [Fact]
    public void ATorrentInBothIsLeftAlone()
    {
        RecoveryPlan plan = Recovery.Plan([Stored(Ubuntu)], [Running(Ubuntu, done: 500, total: 1000)]);

        Assert.Empty(plan.Add);
        Assert.Empty(plan.Stop);
        Assert.Empty(plan.Stage);
        Assert.Equal(Ubuntu, Assert.Single(plan.Carry).InfoHash);
    }

    /// <remarks>
    /// In the store and not in the client: re-added from its magnet. The bytes
    /// are still on disk with their resume file, so this costs a verification
    /// pass rather than the download again — which is the whole reason the
    /// magnet is kept after the client has taken it.
    /// </remarks>
    [Fact]
    public void ATorrentTheClientHasForgottenIsReAddedFromItsMagnet()
    {
        RecoveryPlan plan = Recovery.Plan([Stored(Ubuntu)], []);

        StoredDownload again = Assert.Single(plan.Add);

        Assert.Equal(Ubuntu, again.InfoHash);
        Assert.StartsWith("magnet:?xt=urn:btih:", again.Magnet, StringComparison.Ordinal);
    }

    /// <remarks>
    /// In the client and not in the store: stopped, and <strong>the files are
    /// kept</strong>. Something the plugin has no record of is not something to
    /// delete — it may be half a film the owner has been waiting for, and a
    /// record can be lost by restoring an older database.
    /// </remarks>
    [Fact]
    public void ATorrentTheStoreHasNoRecordOfIsStoppedAndItsFilesKept()
    {
        RecoveryPlan plan = Recovery.Plan([], [Running(Ubuntu, done: 500, total: 1000)]);

        Assert.Equal(Ubuntu, Assert.Single(plan.Stop).InfoHash);
        Assert.Empty(plan.Add);
        Assert.Empty(plan.Stage);
    }

    /// <remarks>
    /// <strong>F4.</strong> 0.3.4 only ever noticed a completion while it was
    /// watching, so a download that finished during a restart sat there for
    /// ever and its episode was never dispatched. Anything already complete on
    /// the first tick is staged.
    /// </remarks>
    [Fact]
    public void ATorrentThatFinishedWhileTheServerWasDownIsStaged()
    {
        RecoveryPlan plan = Recovery.Plan(
            [Stored(Ubuntu, GrabState.Downloading)],
            [Running(Ubuntu, done: 1000, total: 1000)]);

        Assert.Equal(Ubuntu, Assert.Single(plan.Stage).InfoHash);
        Assert.Empty(plan.Carry);
    }

    /// <remarks>
    /// Once it has been staged and dispatched it is finished with, and a
    /// torrent still seeding afterwards is in the client on purpose. A plan
    /// that staged it every tick would dispatch the same episode for ever.
    /// </remarks>
    [Fact]
    public void SomethingAlreadyDoneIsNeitherStagedAgainNorStopped()
    {
        RecoveryPlan plan = Recovery.Plan(
            [Stored(Ubuntu, GrabState.Done)],
            [Running(Ubuntu, done: 1000, total: 1000)]);

        Assert.Empty(plan.Stage);
        Assert.Empty(plan.Stop);
        Assert.Empty(plan.Add);
        Assert.Empty(plan.Carry);
    }

    /// <remarks>
    /// And one that failed is not re-added. It was blacklisted with a reason
    /// and its episode returned to missing; adding it again would fetch the
    /// very thing that was refused.
    /// </remarks>
    [Fact]
    public void SomethingThatFailedIsNotReAdded()
    {
        RecoveryPlan plan = Recovery.Plan([Stored(Ubuntu, GrabState.Failed)], []);

        Assert.Empty(plan.Add);
        Assert.Empty(plan.Stage);
    }

    /// <remarks>
    /// A magnet whose metadata has not arrived has nought bytes done and no
    /// size at all. Comparing two numbers that can both be nought would make it
    /// look finished and stage a torrent with no files in it.
    /// </remarks>
    [Fact]
    public void AMagnetWithNoMetadataYetIsNotMistakenForAFinishedTorrent()
    {
        RecoveryPlan plan = Recovery.Plan(
            [Stored(Ubuntu, GrabState.Grabbed)],
            [Running(Ubuntu, done: 0, total: null)]);

        Assert.Empty(plan.Stage);
        Assert.Single(plan.Carry);
    }

    /// <remarks>
    /// A paused torrent is still the store's and still the client's; it is
    /// waiting for the owner, not lost.
    /// </remarks>
    [Fact]
    public void APausedTorrentIsCarriedRatherThanReAddedOrStopped()
    {
        RecoveryPlan plan = Recovery.Plan(
            [Stored(Ubuntu, GrabState.Paused)],
            [Running(Ubuntu, done: 500, total: 1000, TorrentState.Paused)]);

        Assert.Empty(plan.Add);
        Assert.Empty(plan.Stop);
        Assert.Single(plan.Carry);
    }

    /// <remarks>
    /// All four rows of the table at once, which is what a real restart looks
    /// like.
    /// </remarks>
    [Fact]
    public void EveryCaseAtOnceIsSortedIntoTheRightPile()
    {
        RecoveryPlan plan = Recovery.Plan(
            [
                Stored(Ubuntu, GrabState.Downloading),
                Stored(Archive, GrabState.Downloading),
                Stored(Third, GrabState.Grabbed),
            ],
            [
                Running(Ubuntu, done: 1000, total: 1000),
                Running(Archive, done: 10, total: 1000),
                Running(Stranger, done: 5, total: 50),
            ]);

        Assert.Equal([Third], plan.Add.Select(one => one.InfoHash));
        Assert.Equal([Stranger], plan.Stop.Select(one => one.InfoHash));
        Assert.Equal([Ubuntu], plan.Stage.Select(one => one.InfoHash));
        Assert.Equal([Archive], plan.Carry.Select(one => one.InfoHash));
    }

    /// <remarks>
    /// A hash is forty hex characters and every side of this spells it its own
    /// way. Matching them case-sensitively would have every torrent look both
    /// forgotten and unknown at once — re-added and stopped on the same tick.
    /// </remarks>
    [Fact]
    public void HashesAreMatchedWithoutRegardToCase()
    {
        RecoveryPlan plan = Recovery.Plan(
            [Stored(Ubuntu.ToLowerInvariant())],
            [Running(Ubuntu, done: 5, total: 100)]);

        Assert.Empty(plan.Add);
        Assert.Empty(plan.Stop);
        Assert.Single(plan.Carry);
    }

    private const string Ubuntu = "D160B8D8EA35A5B4E52837468FC8F03D55CEF1F7";

    private const string Archive = "E2720161FF77B42E61D15F4958134DEBAE8D0A96";

    private const string Third = "92D8A3F6864911EF292B4BE0DD5286406396D2B3";

    private const string Stranger = "1111111111111111111111111111111111111111";

    private static StoredDownload Stored(string hash, GrabState state = GrabState.Downloading)
    {
        return new(hash, $"magnet:?xt=urn:btih:{hash}", "Silo S03E06 1080p", state);
    }

    private static TorrentStatus Running(
        string hash,
        long done,
        long? total,
        TorrentState state = TorrentState.Downloading)
    {
        return new(hash, "Silo S03E06 1080p", state, done, total, 0, 0, 0, 0, null, null, null);
    }
}

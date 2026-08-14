using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Activity;

/// <summary>
/// The journal is what every page renders from, and every stage writes to it
/// from whatever thread it happens to be on: harvest fans out over every feed,
/// find over every indexer, and stages two to six run per episode at once.
/// </summary>
public class ActivityJournalTests
{
    /// <remarks>
    /// A plain dictionary and list here would throw, drop entries, or hand out
    /// a half-built snapshot — and it would do it under load, on the owner's
    /// machine, in a stage nobody was watching.
    /// </remarks>
    [Fact]
    public void AThousandEventsAtOnceLeaveNothingTorn()
    {
        ActivityJournal journal = new();

        Parallel.For(0, 1000, index => journal.Started(ActivityStage.Find, $"episode-{index}"));

        ActivitySnapshot snapshot = journal.Snapshot();

        Assert.Equal(1000, snapshot.InFlight.Count);
        Assert.Equal(ActivityJournal.HistoryLimit, snapshot.History.Count);
        Assert.Equal(1000, snapshot.InFlight.Select(activity => activity.Subject).Distinct().Count());
        Assert.Equal(
            snapshot.History.Count,
            snapshot.History.Select(activity => activity.Subject).Distinct().Count());
        Assert.All(snapshot.History, activity => Assert.StartsWith("episode-", activity.Subject, StringComparison.Ordinal));
    }

    /// <remarks>
    /// In flight means still running. A page that keeps showing an episode that
    /// finished an hour ago is worse than one showing nothing, because it is
    /// the page the owner would check to see whether anything is stuck.
    /// </remarks>
    [Fact]
    public void WhatStartedAndFinishedIsNoLongerInFlight()
    {
        ActivityJournal journal = new();

        journal.Started(ActivityStage.Grab, "Silo S02E03");
        Assert.Single(journal.Snapshot().InFlight);

        journal.Finished(ActivityStage.Grab, "Silo S02E03");

        ActivitySnapshot snapshot = journal.Snapshot();

        Assert.Empty(snapshot.InFlight);
        Assert.Equal(2, snapshot.History.Count);
    }

    /// <remarks>
    /// A failure clears the in-flight entry exactly as a success does — a stage
    /// that threw is not still running — but says so in the history, or an
    /// episode simply stops and the journal shows nothing about why.
    /// </remarks>
    [Fact]
    public void WhatFailedIsNoLongerInFlightAndSaysWhy()
    {
        ActivityJournal journal = new();

        journal.Started(ActivityStage.Find, "Silo S02E03");
        journal.Failed(ActivityStage.Find, "Silo S02E03", "every indexer refused");

        ActivitySnapshot snapshot = journal.Snapshot();

        Assert.Empty(snapshot.InFlight);
        ActivityEvent last = snapshot.History[^1];
        Assert.Equal(ActivityOutcome.Failed, last.Outcome);
        Assert.Equal("every indexer refused", last.Detail);
    }

    /// <remarks>
    /// The journal lives as long as the server does. Unbounded history is a
    /// leak that only shows up after a week of running, which is exactly when
    /// nobody is watching it.
    /// </remarks>
    [Fact]
    public void HistoryIsBoundedAndKeepsTheNewest()
    {
        ActivityJournal journal = new();

        for (int index = 0; index < 600; index++)
        {
            journal.Started(ActivityStage.Harvest, $"source-{index}");
        }

        ActivitySnapshot snapshot = journal.Snapshot();

        Assert.Equal(500, snapshot.History.Count);
        Assert.Equal("source-100", snapshot.History[0].Subject);
        Assert.Equal("source-599", snapshot.History[^1].Subject);
    }

    /// <remarks>
    /// A snapshot is handed to a page and then rendered, pushed over the hub and
    /// compared against the last one. If the journal could still change it, two
    /// reads of the same snapshot would disagree and the push that says "this
    /// changed" would be comparing an object against itself.
    /// </remarks>
    [Fact]
    public void ASnapshotAlreadyHandedOutDoesNotChange()
    {
        ActivityJournal journal = new();
        journal.Started(ActivityStage.Names, "Lioness S02E01");

        ActivitySnapshot taken = journal.Snapshot();

        journal.Started(ActivityStage.Names, "Sugar S01E04");
        journal.Finished(ActivityStage.Names, "Lioness S02E01");

        Assert.Single(taken.InFlight);
        Assert.Equal("Lioness S02E01", taken.InFlight[0].Subject);
        Assert.Single(taken.History);
    }

    /// <remarks>
    /// Every stage in the chain, so a stage cannot be added to the pipeline
    /// without a place in the journal to report from. A stage that cannot be
    /// seen does not ship.
    /// </remarks>
    [Fact]
    public void EveryStageOfTheChainCanBeReported()
    {
        Assert.Equal(
            [
                ActivityStage.Harvest,
                ActivityStage.Names,
                ActivityStage.Find,
                ActivityStage.Decide,
                ActivityStage.Grab,
                ActivityStage.Download,
                ActivityStage.Dispatch,
            ],
            Enum.GetValues<ActivityStage>());
    }
}

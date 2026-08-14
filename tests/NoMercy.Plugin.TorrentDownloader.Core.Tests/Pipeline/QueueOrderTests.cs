using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

public class QueueOrderTests
{
    /// <remarks>
    /// The order the Queue page shows is the order the search cadence will
    /// actually ask in — one rule, used by both, or the page is a guess about
    /// what the plugin is going to do.
    /// </remarks>
    [Fact]
    public void WhatHasNeverBeenSearchedComesFirst()
    {
        IReadOnlyList<TrackedEpisode> ordered = QueueOrder.Order(
        [
            Missing(1, 1, 2, lastSearchAt: new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)),
            Missing(1, 1, 1, lastSearchAt: null),
        ]);

        Assert.Equal([new EpisodeKey(1, 1, 1), new EpisodeKey(1, 1, 2)], ordered.Select(episode => episode.Key));
    }

    /// <remarks>
    /// Then the one waiting longest. Anything else lets a popular episode be
    /// asked about repeatedly while an unlucky one is never reached.
    /// </remarks>
    [Fact]
    public void ThenTheOneWaitingLongest()
    {
        IReadOnlyList<TrackedEpisode> ordered = QueueOrder.Order(
        [
            Missing(1, 1, 1, lastSearchAt: new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero)),
            Missing(1, 1, 2, lastSearchAt: new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)),
            Missing(1, 1, 3, lastSearchAt: new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero)),
        ]);

        Assert.Equal([2, 3, 1], ordered.Select(episode => episode.Key.Number));
    }

    /// <remarks>
    /// Two episodes searched at the same moment — which is every episode in a
    /// cycle that ran in one second — are ordered by where they sit, so the
    /// page does not reshuffle itself between two renders of the same data.
    /// </remarks>
    [Fact]
    public void TiesAreBrokenByWhereTheEpisodeSits()
    {
        DateTimeOffset sameMoment = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        IReadOnlyList<TrackedEpisode> ordered = QueueOrder.Order(
        [
            Missing(2, 1, 1, lastSearchAt: sameMoment),
            Missing(1, 2, 1, lastSearchAt: sameMoment),
            Missing(1, 1, 2, lastSearchAt: sameMoment),
            Missing(1, 1, 1, lastSearchAt: sameMoment),
        ]);

        Assert.Equal(
            [new EpisodeKey(1, 1, 1), new EpisodeKey(1, 1, 2), new EpisodeKey(1, 2, 1), new EpisodeKey(2, 1, 1)],
            ordered.Select(episode => episode.Key));
    }

    /// <remarks>
    /// Only what is being looked for. An unaired episode in the search queue
    /// would be asked about before it exists, and an unavailable one has been
    /// given up on until the next maintenance pass puts it back.
    /// </remarks>
    [Fact]
    public void OnlyMissingEpisodesAreInTheQueue()
    {
        IReadOnlyList<TrackedEpisode> ordered = QueueOrder.Order(
        [
            Missing(1, 1, 1),
            Missing(1, 1, 2) with { State = EpisodeState.NotAired },
            Missing(1, 1, 3) with { State = EpisodeState.Unavailable },
        ]);

        Assert.Equal([new EpisodeKey(1, 1, 1)], ordered.Select(episode => episode.Key));
    }

    private static TrackedEpisode Missing(
        int show,
        int season,
        int number,
        DateTimeOffset? lastSearchAt = null)
    {
        return new(
            new(show, season, number),
            "Silo",
            2023,
            LibraryKind.Television,
            "An episode",
            new DateOnly(2026, 1, 1),
            EpisodeState.Missing,
            LastSearchAt: lastSearchAt);
    }
}

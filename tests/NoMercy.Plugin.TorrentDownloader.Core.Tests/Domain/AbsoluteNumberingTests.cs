using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Domain;

public class AbsoluteNumberingTests
{
    /// <remarks>
    /// Anime releases are usually numbered from the start of the series rather
    /// than the season, so episode 13 of season 2 arrives as <c>- 37</c> when
    /// season one had 24. The library numbers by season, and both forms have to
    /// be searchable or half the releases are invisible.
    /// </remarks>
    [Fact]
    public void AnEpisodeIsNumberedFromTheStartOfTheSeries()
    {
        IReadOnlyDictionary<EpisodeKey, int> absolute = AbsoluteNumbering.Build(
            [.. Season(1, 24), .. Season(2, 13)]);

        Assert.Equal(37, absolute[new(1, 2, 13)]);
        Assert.Equal(25, absolute[new(1, 2, 1)]);
        Assert.Equal(1, absolute[new(1, 1, 1)]);
        Assert.Equal(24, absolute[new(1, 1, 24)]);
    }

    /// <remarks>
    /// Season 0 is specials, and specials are not in the absolute sequence. If
    /// they counted, every episode after them would be numbered wrong — and it
    /// would be wrong by however many specials a show happened to have, so the
    /// error would differ per show and look like bad luck rather than a rule.
    /// </remarks>
    [Fact]
    public void SeasonZeroNeitherCountsNorIsNumbered()
    {
        IReadOnlyDictionary<EpisodeKey, int> absolute = AbsoluteNumbering.Build(
            [.. Season(0, 3), .. Season(1, 12), .. Season(2, 4)]);

        Assert.Equal(13, absolute[new(1, 2, 1)]);
        Assert.DoesNotContain(new EpisodeKey(1, 0, 1), absolute.Keys);
    }

    /// <remarks>
    /// A season the library has a hole in is numbered from what is there. It is
    /// the only honest answer available: the plugin cannot know whether the
    /// missing row is an episode that exists and was never imported or one that
    /// never existed, and inventing the larger number would shift every later
    /// season by one.
    /// </remarks>
    [Fact]
    public void ASeasonWithAGapIsNumberedFromTheEpisodesThatExist()
    {
        IReadOnlyDictionary<EpisodeKey, int> absolute = AbsoluteNumbering.Build(
        [
            Episode(1, 1, 1),
            Episode(1, 1, 2),
            Episode(1, 1, 4),
            Episode(1, 2, 1),
        ]);

        Assert.Equal(4, absolute[new(1, 2, 1)]);
    }

    /// <remarks>
    /// The order the library hands them back in is not promised, so the map
    /// cannot depend on it.
    /// </remarks>
    [Fact]
    public void TheOrderTheEpisodesArriveInDoesNotMatter()
    {
        IReadOnlyDictionary<EpisodeKey, int> shuffled = AbsoluteNumbering.Build(
        [
            Episode(1, 2, 2),
            Episode(1, 1, 3),
            Episode(1, 2, 1),
            Episode(1, 1, 1),
            Episode(1, 1, 2),
        ]);

        Assert.Equal(4, shuffled[new(1, 2, 1)]);
        Assert.Equal(5, shuffled[new(1, 2, 2)]);
    }

    /// <remarks>
    /// A show whose seasons do not start at one is numbered from the seasons it
    /// has, not from the numbers they carry.
    /// </remarks>
    [Fact]
    public void SeasonsThatDoNotStartAtOneStillNumberInOrder()
    {
        IReadOnlyDictionary<EpisodeKey, int> absolute = AbsoluteNumbering.Build(
            [.. Season(3, 2), .. Season(7, 2)]);

        Assert.Equal(1, absolute[new(1, 3, 1)]);
        Assert.Equal(3, absolute[new(1, 7, 1)]);
    }

    /// <remarks>
    /// Whether an episode has a file has nothing to do with where it sits in
    /// the series. Counting only what is on disk would renumber the whole show
    /// every time something downloaded.
    /// </remarks>
    [Fact]
    public void WhatIsAlreadyOnDiskStillCounts()
    {
        IReadOnlyDictionary<EpisodeKey, int> absolute = AbsoluteNumbering.Build(
            [.. Season(1, 12, hasFile: true), .. Season(2, 1)]);

        Assert.Equal(13, absolute[new(1, 2, 1)]);
    }

    /// <remarks>
    /// The number is the episode's own plus everything before its season, not
    /// its position in the list. Those agree only while the list is complete,
    /// and they part company exactly when episodes are absent — which is the
    /// case this whole plugin exists for. A running counter would have called
    /// this episode 25.
    /// </remarks>
    [Fact]
    public void AnEpisodeWhoseEarlierSiblingsAreAbsentStillGetsItsOwnNumber()
    {
        IReadOnlyDictionary<EpisodeKey, int> absolute = AbsoluteNumbering.Build(
            [.. Season(1, 24), Episode(1, 2, 13)]);

        Assert.Equal(37, absolute[new(1, 2, 13)]);
    }

    private static IEnumerable<Episode> Season(int season, int count, bool hasFile = false)
    {
        return Enumerable.Range(1, count).Select(number => Episode(1, season, number, hasFile));
    }

    private static Episode Episode(int show, int season, int number, bool hasFile = false)
    {
        return new(new(show, season, number), "An episode", new DateOnly(2020, 1, 1), hasFile);
    }
}

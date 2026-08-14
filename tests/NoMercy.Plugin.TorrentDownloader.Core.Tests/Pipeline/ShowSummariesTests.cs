using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

public class ShowSummariesTests
{
    /// <remarks>
    /// The count is the rows, counted. A total kept anywhere else is a second
    /// number that can disagree with the list under it, which is how 0.3.4 came
    /// to show "0 downloads" while two were running.
    /// </remarks>
    [Fact]
    public void EachCountIsTheRowsInThatState()
    {
        IReadOnlyList<ShowSummary> summaries = ShowSummaries.Summarise(
        [
            Episode(1, 1, 1, EpisodeState.Missing),
            Episode(1, 1, 2, EpisodeState.Missing),
            Episode(1, 1, 3, EpisodeState.NotAired),
            Episode(1, 1, 4, EpisodeState.Unavailable),
        ]);

        ShowSummary show = Assert.Single(summaries);
        Assert.Equal(2, show.Missing);
        Assert.Equal(1, show.WaitingToAir);
        Assert.Equal(1, show.GivenUpForNow);
    }

    /// <remarks>
    /// An unaired episode is never counted as missing. Counting it would have
    /// the plugin claiming work it will not do until the episode exists, and
    /// the number would drop on its own when the date passed.
    /// </remarks>
    [Fact]
    public void AnUnairedEpisodeIsNeverCountedAsMissing()
    {
        ShowSummary show = Assert.Single(ShowSummaries.Summarise(
        [
            Episode(1, 1, 1, EpisodeState.NotAired),
            Episode(1, 1, 2, EpisodeState.NotAired),
        ]));

        Assert.Equal(0, show.Missing);
        Assert.Equal(2, show.WaitingToAir);
    }

    /// <remarks>
    /// The media type travels with the show, so a page can say which library an
    /// episode will go back to without guessing from the title.
    /// </remarks>
    [Fact]
    public void TheMediaTypeAndTheYearTravelWithTheShow()
    {
        IReadOnlyList<ShowSummary> summaries = ShowSummaries.Summarise(
        [
            Episode(1, 1, 1, EpisodeState.Missing, "Silo", 2023, LibraryKind.Television),
            Episode(2, 1, 1, EpisodeState.Missing, "Frieren", 2023, LibraryKind.Anime),
        ]);

        Assert.Equal(LibraryKind.Anime, summaries.Single(show => show.Title == "Frieren").Kind);
        Assert.Equal(LibraryKind.Television, summaries.Single(show => show.Title == "Silo").Kind);
        Assert.Equal(2023, summaries[0].Year);
    }

    [Fact]
    public void ShowsAreListedByTitle()
    {
        IReadOnlyList<ShowSummary> summaries = ShowSummaries.Summarise(
        [
            Episode(1, 1, 1, EpisodeState.Missing, "Silo"),
            Episode(2, 1, 1, EpisodeState.Missing, "frieren"),
            Episode(3, 1, 1, EpisodeState.Missing, "Lioness"),
        ]);

        Assert.Equal(["frieren", "Lioness", "Silo"], summaries.Select(show => show.Title));
    }

    private static TrackedEpisode Episode(
        int show,
        int season,
        int number,
        EpisodeState state,
        string title = "Silo",
        int? year = 2023,
        LibraryKind kind = LibraryKind.Television)
    {
        return new(
            new(show, season, number),
            title,
            year,
            kind,
            "An episode",
            new DateOnly(2026, 1, 1),
            state);
    }
}

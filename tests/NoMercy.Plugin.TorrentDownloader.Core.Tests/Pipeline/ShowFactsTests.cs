using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// What the overview says about a show, counted from the library's own episodes.
/// </summary>
/// <remarks>
/// <strong>Counted here and not read off the server's own column.</strong> On the owner's library on
/// 16 September 2026 every one of the 69 shows read <c>HaveEpisodes = 0</c> while 57 of them had files —
/// 2,264 of them. A column that says nought for a show with four hundred episodes on disk is a column
/// nothing may be decided on.
/// </remarks>
public class ShowFactsTests
{
    private static readonly DateOnly Today = new(2026, 9, 16);

    /// <remarks>
    /// The count is the aired episodes with no file. An episode still to air is not missing — it is not
    /// yet anything — and one already on disk is not either.
    /// </remarks>
    [Fact]
    public void MissingIsTheAiredEpisodesWithNoFile()
    {
        ShowFacts facts = ShowFacts.Of(
            [
                Episode(1, 1, hasFile: true),
                Episode(1, 2, hasFile: false),
                Episode(1, 3, hasFile: false),
                Episode(1, 4, hasFile: false, airs: Today.AddDays(7)),

                // No date at all, which is not aired: the server has no date
                // for an episode that might be next year's.
                new(new(41, 1, 5), "An episode", null, false),
            ],
            Today,
            specials: false);

        Assert.Equal(2, facts.Missing);
    }

    /// <remarks>
    /// Season 0 counts only where the show takes specials, which is the rule the run itself follows
    /// (<c>docs/specs/show-list.md</c>). A column that counted them anyway would ask the owner to chase a
    /// gap the plugin will never fill.
    /// </remarks>
    [Fact]
    public void SpecialsCountOnlyWhereTheShowTakesThem()
    {
        Episode[] episodes = [Episode(1, 1, hasFile: false), Episode(0, 1, hasFile: false)];

        Assert.Equal(1, ShowFacts.Of(episodes, Today, specials: false).Missing);
        Assert.Equal(2, ShowFacts.Of(episodes, Today, specials: true).Missing);
    }

    /// <remarks>
    /// <strong>Whether the owner has the show at all</strong>, which is one video file anywhere in it.
    /// The server keeps a row for every show it ever identified — on the owner's library twelve of the
    /// sixty-nine are shows nobody added, imported on a guess — and those have no file at all.
    /// </remarks>
    [Fact]
    public void AShowIsHeldWhenTheLibraryHasOneFileOfIt()
    {
        Assert.True(ShowFacts.Of([Episode(1, 1, hasFile: true), Episode(1, 2, hasFile: false)], Today, specials: false).Held);
        Assert.False(ShowFacts.Of([Episode(1, 1, hasFile: false), Episode(1, 2, hasFile: false)], Today, specials: false).Held);

        // A file of a special is a file: the owner has the show either way.
        Assert.True(ShowFacts.Of([Episode(0, 1, hasFile: true)], Today, specials: false).Held);
    }

    /// <remarks>A show with no episodes at all is held by nobody and misses nothing, rather than throwing.</remarks>
    [Fact]
    public void AShowWithNoEpisodesMissesNothingAndIsNotHeld()
    {
        ShowFacts facts = ShowFacts.Of([], Today, specials: true);

        Assert.Equal(0, facts.Missing);
        Assert.False(facts.Held);
    }

    private static Episode Episode(int season, int number, bool hasFile, DateOnly? airs = null)
    {
        return new(
            new(41, season, number),
            "An episode",
            airs ?? Today.AddDays(-30),
            hasFile);
    }
}

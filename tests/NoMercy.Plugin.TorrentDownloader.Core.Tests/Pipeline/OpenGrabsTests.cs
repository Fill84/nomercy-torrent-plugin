using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// An episode already being downloaded is not searched for again.
/// </summary>
public class OpenGrabsTests
{
    /// <remarks>
    /// <para>
    /// <strong>Every cycle grabbed the same episode again.</strong> An episode
    /// stays missing until a file for it is in the library, which is right —
    /// and the cycle read that as work to do. On the owner's server on
    /// 23 August 2026 three episodes of Sugar had four identical grabs each,
    /// one per cycle, all carrying the same info hash: the client recognised
    /// the hash and took it once, the store did not, so the Downloads page
    /// showed each of them four times.
    /// </para>
    /// <para>
    /// What is left is what is really outstanding.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnEpisodeWithAnOpenGrabIsNotSearchedForAgain()
    {
        TrackedEpisode[] tracked =
        [
            Episode(203744, 2, 3),
            Episode(203744, 2, 4),
            Episode(203744, 2, 6),
            Episode(278624, 1, 2),
        ];

        IReadOnlyList<TrackedEpisode> left = OpenGrabs.Excluding(
            tracked,
            [new(203744, 2, 3), new(203744, 2, 6)]);

        Assert.Equal(
            [new EpisodeKey(203744, 2, 4), new EpisodeKey(278624, 1, 2)],
            left.Select(one => one.Key));
    }

    /// <remarks>
    /// A pack settles every episode it covers, so none of them is searched for
    /// while it downloads. Searching for the rest of the season would grab the
    /// same season a second time.
    /// </remarks>
    [Fact]
    public void APackSettlesEveryEpisodeItCovers()
    {
        TrackedEpisode[] tracked = [Episode(1, 3, 4), Episode(1, 3, 5), Episode(1, 3, 6)];

        Assert.Empty(OpenGrabs.Excluding(tracked, [new(1, 3, 4), new(1, 3, 5), new(1, 3, 6)]));
    }

    /// <remarks>
    /// Nothing open is nothing excluded, and the list comes back as it went in.
    /// A filter that dropped everything when there was nothing to filter by
    /// would stop the plugin searching at all.
    /// </remarks>
    [Fact]
    public void WithNothingOpenEverythingIsStillSearchedFor()
    {
        TrackedEpisode[] tracked = [Episode(1, 3, 4), Episode(1, 3, 5)];

        Assert.Equal(tracked, OpenGrabs.Excluding(tracked, []));
    }

    private static TrackedEpisode Episode(int show, int season, int number)
    {
        return new(
            new(show, season, number),
            "Sugar",
            2024,
            LibraryKind.Television,
            null,
            new DateOnly(2026, 8, 1),
            EpisodeState.Missing,
            null);
    }
}

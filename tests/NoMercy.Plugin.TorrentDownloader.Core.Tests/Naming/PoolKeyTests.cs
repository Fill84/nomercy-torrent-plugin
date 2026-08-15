using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Naming;

/// <summary>
/// What a harvested name answers for.
/// </summary>
/// <remarks>
/// The key is the only thing joining the stage that reads feeds to the stage
/// that looks for an episode, and neither knows anything about the other. Both
/// have to arrive at the same string from opposite directions, so both
/// directions are asserted here.
/// </remarks>
public class PoolKeyTests
{
    /// <remarks>
    /// The two ways of naming one episode meet: the release name that the feed
    /// carried, and the show and slot the library knows.
    /// </remarks>
    [Fact]
    public void AReleaseAndAnEpisodeArriveAtTheSameKey()
    {
        Assert.Equal(
            PoolKey.For("Silo", 3, 6),
            PoolKey.Of(ReleaseName.Parse(Real("predb.xml", "rss", "Silo.S03E06.720p.WEB.H264-SYLiX"))));
    }

    /// <remarks>
    /// Anime is posted under a number counted from the start of the programme,
    /// and it is a different number from the season's. Keying them the same way
    /// would have episode 1172 answer for season 11 episode 72.
    /// </remarks>
    [Fact]
    public void AnAbsoluteNumberKeysApartFromASeasonAndEpisode()
    {
        string key = PoolKey.Of(ReleaseName.Parse(Real(
            "nyaa-absolute.xml",
            "torrent-rss",
            "[Naruto-Kun.Hu] One Piece (Elbaf arc) - 1172 [1080p].mkv")))!;

        Assert.Equal(PoolKey.ForAbsolute("One Piece (Elbaf arc)", 1172), key);
        Assert.NotEqual(PoolKey.For("One Piece (Elbaf arc)", 11, 72), key);
    }

    /// <remarks>
    /// A pack answers for a season, not for an episode of one. Which gaps it
    /// fills is decided later and by something that knows what is missing; a
    /// stage reading a feed does not.
    /// </remarks>
    [Fact]
    public void APackKeysUnderItsSeason()
    {
        Assert.Equal(
            PoolKey.ForSeason("Pokemon Master Quest", 5),
            PoolKey.Of(ReleaseName.Parse(Real(
                "nyaa-diacritic.xml",
                "torrent-rss",
                "[T3KASHi] Pokemon Master Quest S05 TRUEFRENCH 1080p WEB-DL H.264 (VF)"))));
    }

    /// <remarks>
    /// A name that answers for no episode has no key. Half a scene feed is
    /// films, and one kept under its title alone would be compared against
    /// every episode of every show for ever and match none of them.
    /// </remarks>
    [Fact]
    public void ANameThatAnswersForNoEpisodeHasNoKey()
    {
        Assert.Null(PoolKey.Of(ReleaseName.Parse(Real(
            "scenesource.xml",
            "rss",
            "Abrahams Boys 2025 BluRay 1080p DDP 5 1 x264-hallowed"))));
    }

    /// <remarks>
    /// How a title is spelt is not part of the key, and it cannot be: one Nyaa
    /// page carries this one episode under three spellings of the same
    /// programme, differing only in what became of the apostrophe. Three keys
    /// for one episode means the resolver finds a third of the names that were
    /// harvested for it.
    /// </remarks>
    [Fact]
    public void EverySpellingOfOneEpisodeKeysTheSame()
    {
        string[] spellings =
        [
            // The apostrophe as a dot, as nothing at all, and as itself.
            Real("nyaa.xml", "torrent-rss", "Frieren.Beyond.Journey.s.End.S01E13.MULTi.1080p.WEB.x264-T3KASHi"),
            Real("nyaa.xml", "torrent-rss", "Frieren Beyond Journeys End S01E13 Hatred of Ones Kind 1080p AMZN WEB-DL DDP2.0 H 264-VARYG (Sousou no Frieren, Multi-Subs)"),
            Real("nyaa.xml", "torrent-rss", "[ToonsHub] Frieren- Beyond Journey's End S01E13 Aversion to One's Own Kind 1080p CR WEB-DL x264 (Multi-Audio, Multi-Subs)"),
        ];

        string[] keys = [.. spellings.Select(name => PoolKey.Of(ReleaseName.Parse(name))!)];

        Assert.Single(keys.Distinct(StringComparer.Ordinal));

        // And it is the key the library's own title arrives at, which is the
        // spelling the resolver will look it up under.
        Assert.Equal(PoolKey.For("Frieren: Beyond Journey's End", 1, 13), keys[0]);
    }

    /// <remarks>
    /// Nor is an accent, for the same reason and on the same page.
    /// </remarks>
    [Fact]
    public void AnAccentIsNotPartOfTheKeyEither()
    {
        Assert.Equal(PoolKey.For("Pokémon Horizons", 1, 1), PoolKey.For("Pokemon Horizons", 1, 1));
    }

    private static string Real(string fixture, string reader, string name)
    {
        Assert.Contains(name, Capture.Rows(fixture, reader));

        return name;
    }
}

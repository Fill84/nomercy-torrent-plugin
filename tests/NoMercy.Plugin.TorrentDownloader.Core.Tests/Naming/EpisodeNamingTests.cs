using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Naming;

/// <summary>
/// Whether a release name names one episode, against names the sites really published.
/// </summary>
/// <remarks>
/// It joins what a name source printed to what the library knows, and neither knows anything about the
/// other: <c>Silo.S03E06.720p.WEB.H264-SYLiX</c> from one side, show Silo season 3 episode 6 from the
/// other. <c>docs/specs/release-names.md</c>.
/// </remarks>
public sealed class EpisodeNamingTests
{
    [Fact]
    public void AReleaseNamesItsEpisodeAndNoOther()
    {
        ReleaseName name = Parsed("predb.xml", "rss", "Silo.S03E06.720p.WEB.H264-SYLiX");

        Assert.True(EpisodeNaming.Names(name, "Silo", new(41, 3, 6)));
        Assert.False(EpisodeNaming.Names(name, "Silo", new(41, 3, 7)));
        Assert.False(EpisodeNaming.Names(name, "Silo", new(41, 2, 6)));
        Assert.False(EpisodeNaming.Names(name, "Silos", new(41, 3, 6)));
    }

    /// <remarks>
    /// Anime is posted under a number counted from the start of the programme, and it is a different
    /// number from the season's. Episode 1172 does not name season 11 episode 72, and without a season
    /// and episode number it names no episode at all.
    /// </remarks>
    [Fact]
    public void AnAbsoluteNumberNamesNoEpisode()
    {
        ReleaseName name = Parsed("nyaa-absolute.xml", "torrent-rss", "[Naruto-Kun.Hu] One Piece (Elbaf arc) - 1172 [1080p].mkv");

        Assert.False(EpisodeNaming.Names(name, "One Piece (Elbaf arc)", new(21, 11, 72)));
        Assert.False(EpisodeNaming.Names(name, "One Piece (Elbaf arc)", new(21, 1, 1172)));
    }

    /// <remarks>A pack names a season, and a release name without an episode number is not searched for.</remarks>
    [Fact]
    public void APackNamesNoEpisode()
    {
        ReleaseName name = Parsed("nyaa-diacritic.xml", "torrent-rss", "[T3KASHi] Pokemon Master Quest S05 TRUEFRENCH 1080p WEB-DL H.264 (VF)");

        Assert.False(EpisodeNaming.Names(name, "Pokemon Master Quest", new(2201, 5, 1)));
    }

    /// <remarks>A name for a run of episodes names none of them: a release name names one episode.</remarks>
    [Fact]
    public void ARunOfEpisodesNamesNoneOfThem()
    {
        ReleaseName name = Parsed(
            "nyaa-diacritic.xml",
            "torrent-rss",
            "Pokemon Horizons The Series S01E112-E123 1080p NF WEB-DL MULTi AAC2.0 H 264-VARYG (Pocket Monsters (2023), Multi-Audio, Multi-Subs)");

        Assert.False(EpisodeNaming.Names(name, "Pokémon Horizons: The Series", new(2202, 1, 112)));
    }

    /// <remarks>Half a scene feed is films, and a film names no episode of anything.</remarks>
    [Fact]
    public void AFilmNamesNoEpisode()
    {
        ReleaseName name = Parsed("scenesource.xml", "rss", "Abrahams Boys 2025 BluRay 1080p DDP 5 1 x264-hallowed");

        Assert.False(EpisodeNaming.Names(name, "Abrahams Boys", new(9, 1, 1)));
    }

    /// <remarks>
    /// How a title is spelt does not decide it: one Nyaa page carries this one episode under three
    /// spellings of the same programme, differing only in what became of the apostrophe, and each names
    /// the library's own title.
    /// </remarks>
    [Fact]
    public void EverySpellingOfOneEpisodeNamesIt()
    {
        string[] spellings =
        [
            "Frieren.Beyond.Journey.s.End.S01E13.MULTi.1080p.WEB.x264-T3KASHi",
            "Frieren Beyond Journeys End S01E13 Hatred of Ones Kind 1080p AMZN WEB-DL DDP2.0 H 264-VARYG (Sousou no Frieren, Multi-Subs)",
            "[ToonsHub] Frieren- Beyond Journey's End S01E13 Aversion to One's Own Kind 1080p CR WEB-DL x264 (Multi-Audio, Multi-Subs)",
        ];

        foreach (string spelling in spellings)
        {
            Assert.True(
                EpisodeNaming.Names(Parsed("nyaa.xml", "torrent-rss", spelling), "Frieren: Beyond Journey's End", new(7, 1, 13)),
                spelling);
        }
    }

    /// <remarks>A year after the title is how a one-word programme is told apart, and it still names the show.</remarks>
    [Fact]
    public void AYearAfterTheTitleStillNamesTheShow()
    {
        ReleaseName name = Parsed("sugar1-apibay.json", "apibay", "Sugar 2024 S02E01 Home Away from Home 720p ATVP WEB-DL DDP5 1 H 264-NTb");

        Assert.True(EpisodeNaming.Names(name, "Sugar", new(5, 2, 1)));
    }

    private static ReleaseName Parsed(string fixture, string reader, string name)
    {
        Assert.Contains(name, Capture.Rows(fixture, reader));

        return ReleaseName.Parse(name);
    }
}

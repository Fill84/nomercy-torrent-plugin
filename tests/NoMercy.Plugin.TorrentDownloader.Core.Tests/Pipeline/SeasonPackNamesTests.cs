using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

using Xunit;

using static NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport.NameSourceSites;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// The release names of a season pack: from the feeds, from the name sources' search for the season, and
/// judged and dated as an episode's name is.
/// </summary>
/// <remarks>
/// <para>
/// GitHub issue #1, on the owner's word of 14 October 2026, which replaced the owner's rule of 15 September 2026
/// that a pack is not searched for: when two or more episodes of one season are searched for, a pack of that
/// season is looked for first (<c>docs/specs/release-names.md</c> § Season packs).
/// </para>
/// <para>
/// The feeds are what they carried on 15 September 2026. The searches are the four name sources asked
/// <c>Adolescence S01</c> on 14 October 2026: SceneSource answers with FLUX's 1080p pack, srrDB with
/// HiggsBoson's two DIRFIX packs and the season's single episodes, PreDB and PreDB.net with single episodes
/// only.
/// </para>
/// </remarks>
public sealed class SeasonPackNamesTests
{
    public const string FluxPack = "Adolescence S01 1080p NF WEB-DL DDP5.1 Atmos H.264-FLUX";
    public const string MultiPack = "Adolescence.S01.DIRFIX.MULTI.1080p.WEB.X264-HiggsBoson";
    public const string FrenchPack = "Adolescence.S01.DIRFIX.FRENCH.720p.WEB.X264-HiggsBoson";

    public const string SceneSourceSeason = "https://www.scnsrc.me/feed/?s=Adolescence+S01";
    public const string PreDbSeason = "https://predb.me/?search=Adolescence+S01&rss=1";
    public const string PreDbNetSeason = "https://api.predb.net/feed/?q=Adolescence+S01";
    public const string SrrDbSeason = "https://api.srrdb.com/v1/search/adolescence-s01";

    private const string ForeverHomePack = "Forever Home S01 1080p MY5 WEB-DL AAC2.0 H.264-TBN";

    /// <remarks>
    /// SceneSource's feed of 15 September 2026 carried TBN's pack of Forever Home's first season. With two of
    /// that season's episodes searched for it is the season's pack name; it is still no episode's name.
    /// </remarks>
    [Fact]
    public async Task AFeedPackNameIsTakenForASeasonWithSeveralGaps()
    {
        FeedNamesTaken taken = await Over(new FakeFetch().AnsweringFeeds()).ReadFeedsAsync(
            [Episode(3, "Forever Home", 1, 1), Episode(3, "Forever Home", 1, 2)],
            CancellationToken.None);

        Assert.Equal([ForeverHomePack], taken.PacksFor(3, 1).Select(name => name.Title));
        Assert.Equal(["SceneSource"], taken.PacksFor(3, 1).Select(name => name.Source));
        Assert.Empty(taken.All);
    }

    /// <remarks>
    /// With one episode of the season searched for no pack is looked for, so the feed's pack is not taken.
    /// </remarks>
    [Fact]
    public async Task AFeedPackNameIsNotTakenForASeasonWithOneGap()
    {
        FeedNamesTaken taken = await Over(new FakeFetch().AnsweringFeeds()).ReadFeedsAsync(
            [Episode(3, "Forever Home", 1, 1)],
            CancellationToken.None);

        Assert.Empty(taken.PacksFor(3, 1));
    }

    /// <remarks>
    /// No feed of the day names a pack of Adolescence, so every name source's search is asked for the show and
    /// the season, and of everything they answer only the packs of that season are kept: srrDB's single
    /// episodes are left for the episodes' own search.
    /// </remarks>
    [Fact]
    public async Task ASeasonNoFeedNamedIsLookedUpInEverySourcesSearch()
    {
        FakeFetch fetch = new FakeFetch().AnsweringFeeds().AnsweringAdolescenceSeason();
        NameSources sources = Over(fetch);
        TrackedEpisode[] season = [Adolescence(1), Adolescence(2), Adolescence(3)];

        FeedNamesTaken taken = await sources.ReadFeedsAsync(season, CancellationToken.None);
        IReadOnlyList<SourceName> names = await sources.PackNamesForAsync(season, taken, CancellationToken.None);

        Assert.Equal(
            [PreDbNetSeason, SrrDbSeason, PreDbSeason, SceneSourceSeason],
            fetch.Asked.Select(address => address.ToString()).Where(address => !Feeds.Contains(address)).Order(StringComparer.Ordinal));

        Assert.Equal(
            [FluxPack, FrenchPack, MultiPack],
            names.Select(name => name.Title).Distinct().Order(StringComparer.Ordinal));

        Assert.Contains(names, name => name.Title == FluxPack && name.Source == "SceneSource");
        Assert.Contains(
            Capture.Rows("names-srrdb-search-adolescence-s01.json", "srrdb"),
            title => title == "Adolescence.S01E01.1080p.WEB.H264-SuccessfulCrab");
    }

    /// <remarks>
    /// A pack name is dated against the latest episode of the season it would cover. FLUX's pack was published
    /// on 13 March 2025, the day the season aired; had the third episode aired on 1 April it could not be a
    /// pack of all three, and HiggsBoson's of 13 May still could. Dated against the first episode instead, both
    /// would be kept.
    /// </remarks>
    [Fact]
    public async Task APackNameIsDatedAgainstTheLatestEpisodeItWouldCover()
    {
        FakeFetch fetch = new FakeFetch().AnsweringFeeds().AnsweringAdolescenceSeason();
        NameSources sources = Over(fetch);
        TrackedEpisode[] season = [Adolescence(1), Adolescence(2), Adolescence(3) with { AirDate = new DateOnly(2025, 4, 1) }];

        FeedNamesTaken taken = await sources.ReadFeedsAsync(season, CancellationToken.None);
        IReadOnlyList<SourceName> names = await sources.PackNamesForAsync(season, taken, CancellationToken.None);

        Assert.DoesNotContain(names, name => name.Title == FluxPack);
        Assert.Contains(names, name => name.Title == MultiPack);
    }

    /// <remarks>
    /// Judged against the show's settings exactly as an episode's name is: FLUX's pack is 1080p and h264, and
    /// carries no language; HiggsBoson's is MULTi, which English only refuses, and its French pack is 720p. A
    /// pack of another season is not this season's.
    /// </remarks>
    [Fact]
    public void APackNameIsJudgedAgainstTheShowsSettings()
    {
        NameJudge judge = new(new() { Quality = "1080p", Codec = "h264", EnglishOnly = true, Searched = true });

        Assert.True(judge.JudgePack(ReleaseName.Parse(FluxPack), Adolescence(3), Blacklist.None).Accepted);
        Assert.Contains("MULTi", judge.JudgePack(ReleaseName.Parse(MultiPack), Adolescence(3), Blacklist.None).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("720p is not 1080p.", judge.JudgePack(ReleaseName.Parse(FrenchPack), Adolescence(3), Blacklist.None).Reason);
        Assert.False(judge.JudgePack(ReleaseName.Parse(FluxPack), Adolescence(3) with { Key = new(77, 2, 3) }, Blacklist.None).Accepted);
        Assert.False(judge.JudgeName(ReleaseName.Parse(FluxPack), Adolescence(3), Blacklist.None).Accepted);
    }

    /// <summary>An episode of Adolescence's one season, on the day all four aired.</summary>
    public static TrackedEpisode Adolescence(int number)
    {
        return Episode(77, "Adolescence", 1, number, year: 2025) with { AirDate = new DateOnly(2025, 3, 13) };
    }
}

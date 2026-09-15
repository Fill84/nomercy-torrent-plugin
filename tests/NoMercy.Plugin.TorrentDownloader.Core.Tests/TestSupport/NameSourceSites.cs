using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

/// <summary>
/// The four name sources as <c>sources.json</c> ships them, answering with the pages captured from them
/// on 15 September 2026.
/// </summary>
/// <remarks>
/// The feeds are what each carried that day; the searches are each one asked about Silo S02E01, which
/// all four answer. <c>docs/specs/release-names.md</c>.
/// </remarks>
public static class NameSourceSites
{
    public const string PreDbFeed = "https://predb.me/?rss=1";
    public const string SrrDbFeed = "https://www.srrdb.com/feed/srrs";
    public const string PreDbNetFeed = "https://api.predb.net/feed/";
    public const string SceneSourceFeed = "https://www.scnsrc.me/feed/";

    public const string PreDbSearch = "https://predb.me/?search=Silo+S02E01&rss=1";
    public const string SrrDbSearch = "https://api.srrdb.com/v1/search/silo-s02e01";
    public const string PreDbNetSearch = "https://api.predb.net/feed/?q=Silo+S02E01";
    public const string SceneSourceSearch = "https://www.scnsrc.me/feed/?s=Silo+S02E01";

    public static readonly string[] Feeds = [PreDbFeed, SrrDbFeed, PreDbNetFeed, SceneSourceFeed];

    public static readonly string[] SiloSearches = [PreDbSearch, SrrDbSearch, PreDbNetSearch, SceneSourceSearch];

    /// <summary>The five entries of the four name sources, copied from <c>sources.json</c>.</summary>
    public static readonly SourceDefinition[] Shipped =
    [
        new("PreDB", "rss", PreDbFeed)
        {
            SearchUrl = "https://predb.me/?search={query}&rss=1",
            SearchGated = true,
            Priority = 20,
        },
        new("srrDB", "rss", SrrDbFeed) { Priority = 20 },
        new("srrDB search", "srrdb", "https://api.srrdb.com/v1/search/{query}")
        {
            Query = QueryStyles.Slug,
            Priority = 20,
        },
        new("PreDB.net", "rss", PreDbNetFeed)
        {
            SearchUrl = "https://api.predb.net/feed/?q={query}",
            Priority = 20,
        },
        new("SceneSource", "rss", SceneSourceFeed)
        {
            SearchUrl = "https://www.scnsrc.me/feed/?s={query}",
            Gated = true,
            SearchGated = true,
            Priority = 20,
        },
    ];

    /// <summary>Nyaa as <c>sources.json</c> ships it: an indexer, for anime libraries.</summary>
    public static readonly SourceDefinition Nyaa = new("Nyaa", "torrent-rss", "https://nyaa.si/?page=rss&q={query}")
    {
        Libraries = ["anime"],
        Priority = 70,
    };

    /// <summary>Every feed answering with what it carried on the day.</summary>
    public static FakeFetch AnsweringFeeds(this FakeFetch fetch, TimeSpan? takes = null)
    {
        return fetch
            .Answers(PreDbFeed, Capture.Fixture("names-predb-feed.xml"), takes)
            .Answers(SrrDbFeed, Capture.Fixture("names-srrdb-feed.xml"), takes)
            .Answers(PreDbNetFeed, Capture.Fixture("names-predbnet-feed.xml"), takes)
            .Answers(SceneSourceFeed, Capture.Fixture("names-scenesource-feed.xml"), takes);
    }

    /// <summary>Every search answering what it answered when asked about Silo S02E01.</summary>
    public static FakeFetch AnsweringSiloSearches(this FakeFetch fetch)
    {
        return fetch
            .Answers(PreDbSearch, Capture.Fixture("names-predb-search-silo-s02e01.xml"))
            .Answers(SrrDbSearch, Capture.Fixture("names-srrdb-search-silo-s02e01.json"))
            .Answers(PreDbNetSearch, Capture.Fixture("names-predbnet-search-silo-s02e01.xml"))
            .Answers(SceneSourceSearch, Capture.Fixture("names-scenesource-search-silo-s02e01.xml"));
    }

    public static NameSources Over(
        FakeFetch fetch,
        IActivityJournal? journal = null,
        TimeProvider? clock = null,
        ISourceLedger? ledger = null,
        IEnumerable<SourceDefinition>? sources = null)
    {
        return new(
            SourceCatalogue.Build(sources ?? Shipped, [], []),
            fetch,
            Readers.Shipped(),
            journal ?? new ActivityJournal(),
            clock ?? TimeProvider.System,
            ledger);
    }

    public static TrackedEpisode Episode(
        int showId,
        string show,
        int season,
        int number,
        EpisodeState state = EpisodeState.Missing,
        int? year = null,
        LibraryKind kind = LibraryKind.Television,
        int? absolute = null)
    {
        return new(new(showId, season, number), show, year, kind, null, new DateOnly(2026, 1, 1), state, absolute);
    }
}

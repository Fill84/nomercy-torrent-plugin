using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

/// <summary>
/// The indexers as <c>sources.json</c> ships them, answering one exact release name with the pages
/// captured from them on 15 September 2026.
/// </summary>
/// <remarks>
/// <para>
/// <c>Silo.S02E01.1080p.WEB.H264-SuccessfulCrab</c>, asked of every television indexer, and
/// <c>Solo.Leveling.S02E01.1080p.WEB.H264-SKYANiME</c> of the three first-choice indexers for anime. The
/// release was posted twice: once through TGx (hash 87D8…), which six indexers list with a readable
/// torrent, and once through EZTV (41B0…), which three do. Torrentz2 also lists two more uploads of the
/// name, each on it alone. EZTV's own page for its row answered 451, and TorrentBay names its torrents only
/// to a signed request that no test here answers — so neither is readable here.
/// </para>
/// <para>
/// TorrentBay is declared with one page here where it ships with three: the captures are its first page,
/// and a second page scripted from nothing would be a page nobody captured.
/// </para>
/// </remarks>
public static class IndexerSites
{
    public const string Silo = "Silo.S02E01.1080p.WEB.H264-SuccessfulCrab";
    public const string SiloWords = "Silo+S02E01+1080p+WEB+H264+SuccessfulCrab";
    public const string SoloLeveling = "Solo.Leveling.S02E01.1080p.WEB.H264-SKYANiME";

    public const string TgxHash = "87D8113208929B518DCE9EADDCA3E4C74AC72F38";
    public const string EztvHash = "41B0C4067947D7C0BA6FBB1BF2AA8A1C19223D74";
    public const string PlainHash = "1A57E3F36ABCBFB0B3AA6A67520A77216048BAE5";
    public const string SecondPlainHash = "4C74774FECABF07B834CAAA015A79DE5E599F2A5";

    public static readonly SourceDefinition ThePirateBay = new("The Pirate Bay", "apibay", "https://apibay.org/q.php?q={query}&cat=") { Priority = 45 };

    public static readonly SourceDefinition X1337 = new("1337x", "site", "https://www.1337x.to/sort-category-search/{query}/TV/time/desc/1/")
    {
        Reader = "1337x",
        Gated = true,
        Priority = 40,
    };

    public static readonly SourceDefinition LimeTorrents = new("LimeTorrents", "site", "https://www.limetorrents.lol/search/all/{query}/")
    {
        Priority = 35,
        FirstChoice = 3,
    };

    public static readonly SourceDefinition TorrentBay = new("TorrentBay", "site", "https://extranet.torrentbay.st/browse/?q={query}&sort=seeders&order=desc")
    {
        Reader = "torrentbay",
        Gated = true,
        Priority = 60,
        FirstChoice = 2,
    };

    public static readonly SourceDefinition Eztv = new("EZTV", "site", "https://eztvx.to/search/{query}")
    {
        Reader = "eztv",
        Gated = true,
        Priority = 30,
    };

    public static readonly SourceDefinition TorrentGalaxy = new("TorrentGalaxy", "site", "https://torrentgalaxy.one/get-posts/keywords:{query}/")
    {
        Reader = "torrentgalaxy",
        Query = QueryStyles.Spaced,
        Priority = 30,
    };

    public static readonly SourceDefinition Torrentz2 = new("Torrentz2", "site", "https://torrentz2.nz/search?q={query}")
    {
        Reader = "torrentz2",
        Priority = 25,
    };

    public static readonly SourceDefinition TorrentDownloads = new("TorrentDownloads", "site", "https://www.torrentdownloads.pro/search/?search={query}")
    {
        Reader = "torrentdownloads",
        Priority = 25,
    };

    public static readonly SourceDefinition Nyaa = new("Nyaa", "torrent-rss", "https://nyaa.si/?page=rss&q={query}")
    {
        Libraries = ["anime"],
        Priority = 70,
        FirstChoice = 1,
    };

    /// <summary>Every indexer, in the order <c>sources.json</c> lists them.</summary>
    public static readonly SourceDefinition[] Shipped =
        [ThePirateBay, X1337, LimeTorrents, TorrentBay, Eztv, TorrentGalaxy, Torrentz2, TorrentDownloads, Nyaa];

    /// <summary>The detail pages of the rows that carry no hash, as their listings link them.</summary>
    public const string X1337Detail = "https://www.1337x.to/torrent/6265138/Silo-S02E01-1080p-WEB-H264-SuccessfulCrab-TGx/";
    public const string TorrentGalaxyTgxDetail = "https://torrentgalaxy.one/post-detail/7381b1/silo-s02e01-1080p-web-h264-successfulcrab-tgx/";
    public const string TorrentGalaxyEztvDetail = "https://torrentgalaxy.one/post-detail/79ab7b/silo-s02e01-1080p-web-h264-successfulcrab-eztv/";
    public const string Torrentz2TgxDetail = "https://torrentz2.nz/torrent/673bad15fba18ebb7b56b7cb";
    public const string Torrentz2EztvxDetail = "https://torrentz2.nz/torrent/673f9007fba18ebb7b5a07ef";
    public const string Torrentz2PlainDetail = "https://torrentz2.nz/torrent/6741f0aefba18ebb7b5b3cba";
    public const string Torrentz2SecondPlainDetail = "https://torrentz2.nz/torrent/6a40ef16906341a7aafe0a73";
    public const string TorrentDownloadsDetail = "https://www.torrentdownloads.pro/torrent/1704988607/Silo-S02E01-1080p-WEB-H264-SuccessfulCrab%5BTGx%5D";
    public const string EztvDetail = "https://eztvx.to/ep/2394161/silo-s02e01-1080p-web-h264-successfulcrab/?d=";

    /// <summary>The exact-name search address of one indexer.</summary>
    public static string Exact(SourceDefinition indexer, string name)
    {
        return new Uri(Query.WriteExact(indexer.SearchAddress!, name)).ToString();
    }

    /// <summary>The same name without its punctuation, in the indexer's own style.</summary>
    public static string Words(SourceDefinition indexer, string name)
    {
        return new Uri(Query.Write(indexer.SearchAddress!, name, indexer.Query)).ToString();
    }

    /// <summary>
    /// Every television indexer answering Silo's name as it did on the day.
    /// </summary>
    /// <param name="fetch">The fake to script.</param>
    /// <param name="restTakes">How long each indexer after the first-choice ones takes to answer the exact name.</param>
    public static FakeFetch AnsweringSilo(this FakeFetch fetch, TimeSpan? restTakes = null)
    {
        foreach ((SourceDefinition indexer, string exact, string words) in (
                     (SourceDefinition, string, string)[])
                 [
                     (TorrentBay, "round-silo-torrentbay-exact.html", "round-silo-torrentbay-words.html"),
                     (LimeTorrents, "round-silo-limetorrents-exact.html", "round-silo-limetorrents-words.html"),
                     (ThePirateBay, "round-silo-apibay-exact.json", "round-silo-apibay-words.json"),
                     (X1337, "round-silo-1337x-exact.html", "round-silo-1337x-words.html"),
                     (Eztv, "round-silo-eztv-exact.html", "round-silo-eztv-words.html"),
                     (TorrentGalaxy, "round-silo-torrentgalaxy-exact.html", "round-silo-torrentgalaxy-words.html"),
                     (Torrentz2, "round-silo-torrentz2-exact.html", "round-silo-torrentz2-words.html"),
                     (TorrentDownloads, "round-silo-torrentdownloads-exact.html", "round-silo-torrentdownloads-words.html"),
                 ])
        {
            TimeSpan? takes = indexer.FirstChoice is null ? restTakes : null;

            fetch.Answers(Exact(indexer, Silo), Capture.Fixture(exact), takes);
            fetch.Answers(Words(indexer, Silo), Capture.Fixture(words));
        }

        return fetch
            .Answers(X1337Detail, Capture.Fixture("round-silo-1337x-detail-tgx.html"))
            .Answers(TorrentGalaxyTgxDetail, Capture.Fixture("round-silo-torrentgalaxy-detail-tgx.html"))
            .Answers(TorrentGalaxyEztvDetail, Capture.Fixture("round-silo-torrentgalaxy-detail-eztv.html"))
            .Answers(Torrentz2TgxDetail, Capture.Fixture("round-silo-torrentz2-detail-tgx.html"))
            .Answers(Torrentz2EztvxDetail, Capture.Fixture("round-silo-torrentz2-detail-eztvx.html"))
            .Answers(Torrentz2PlainDetail, Capture.Fixture("round-silo-torrentz2-detail-plain.html"))
            .Answers(Torrentz2SecondPlainDetail, Capture.Fixture("round-silo-torrentz2-detail-plain2.html"))
            .Answers(new Uri(TorrentDownloadsDetail).ToString(), Capture.Fixture("round-silo-torrentdownloads-detail-tgx.html"))
            .Answers(TorrentDownloadsDetail, Capture.Fixture("round-silo-torrentdownloads-detail-tgx.html"))
            .Fails(EztvDetail, FetchOutcome.Refused, "eztvx.to answered 451.");
    }

    /// <summary>The three first-choice indexers answering the anime name as they did on the day.</summary>
    public static FakeFetch AnsweringSoloLeveling(this FakeFetch fetch)
    {
        return fetch
            .Answers(Exact(Nyaa, SoloLeveling), Capture.Fixture("round-solo-nyaa-exact.xml"))
            .Answers(Words(Nyaa, SoloLeveling), Capture.Fixture("round-solo-nyaa-words.xml"))
            .Answers(Exact(TorrentBay, SoloLeveling), Capture.Fixture("round-solo-torrentbay-exact.html"))
            .Answers(Words(TorrentBay, SoloLeveling), Capture.Fixture("round-solo-torrentbay-words.html"))
            .Answers(Exact(LimeTorrents, SoloLeveling), Capture.Fixture("round-solo-limetorrents-exact.html"))
            .Answers(Words(LimeTorrents, SoloLeveling), Capture.Fixture("round-solo-limetorrents-words.html"));
    }

    public static Find Finding(FakeFetch fetch, IEnumerable<SourceDefinition>? sources = null, IActivityJournal? journal = null, RecordingLedger? ledger = null)
    {
        return new(SourceCatalogue.Build(sources ?? Shipped, [], []), fetch, Readers.Shipped(), journal ?? new ActivityJournal(), ledger);
    }

    public static IndexerRound Round(FakeFetch fetch, IEnumerable<SourceDefinition>? sources = null, IActivityJournal? journal = null)
    {
        IActivityJournal writing = journal ?? new ActivityJournal();

        return new(Finding(fetch, sources, writing), writing);
    }

    public static TrackedEpisode SiloEpisode { get; } =
        new(new(41, 2, 1), "Silo", 2023, LibraryKind.Television, null, new DateOnly(2024, 11, 15), EpisodeState.Missing);

    public static TrackedEpisode SoloLevelingEpisode { get; } =
        new(new(88, 2, 1), "Solo Leveling", 2024, LibraryKind.Anime, null, new DateOnly(2025, 1, 5), EpisodeState.Missing, 13);
}

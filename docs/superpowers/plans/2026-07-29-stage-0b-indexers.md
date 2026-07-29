# Stage 0b: Indexers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fetch release candidates from the outside world through one interface, over protocols that do not break when a website changes its markup, without getting the user's indexer account banned.

**Architecture:** An `IIndexer` port with two implementations — a generic RSS/scene-feed reader and a Torznab client — plus an aggregator that fans out, paces requests per indexer, survives individual failures, and merges results. Parsing is separated from fetching throughout: every parser is a pure function over a string, tested against **real captured responses** in `tests/fixtures/`, and only the thin client classes touch `HttpClient`.

**Tech Stack:** .NET 10, C# 13, `System.Xml.Linq` (no feed library), xUnit, FluentAssertions.

## Global Constraints

Identical to Stage 0a, repeated so this plan stands alone.

- **Target framework `net10.0`.**
- **Explicit types, never `var`.** Hard rule.
- **No useless comments.** Default zero — comment only a constraint a reader could not infer.
- **No license header in this repo.**
- **`[GeneratedRegex]` for every constant pattern**, `partial` class, `partial static` method. Case-insensitive patterns carry `RegexOptions.IgnoreCase | RegexOptions.CultureInvariant`. Runtime-constructed regexes over user-supplied patterns go through `TermMatcher`.
- **`Core` keeps zero reference to `NoMercy.Plugins.Abstractions`, `NoMercy.Events`, or any NoMercy assembly.**
- **Parsers do no I/O.** `Core.Indexers.Parsing` is pure: no `HttpClient`, no `File`, no `DateTime.Now`. Only the client classes in `Core.Indexers` hold an `HttpClient`, and it is **injected**, never constructed — the plugin shell supplies the host's allowlisted client.
- **No `DateTime.Now`/`DateTimeOffset.UtcNow` anywhere in `Core`.** Time comes from an injected `IClock`. Rate limiting and circuit breaking are untestable otherwise.
- FluentAssertions pinned `[7.0.0,8.0.0)`.
- Conventional commits on `master`. **No attribution trailers of any kind.**
- Culture-invariant string comparison against machine text throughout.

**Spec:** `docs/superpowers/specs/2026-07-29-torrent-download-plugin-design.md` §9.1 (indexers), §7.2 (discovery tiers), §11 (cadences).

## What Stage 0a already provides

`ReleaseInfo` (`IndexerName`, `TorrentId`, `Title`, `DetailUrl`, `MagnetUri`, `DownloadUrl`, `InfoHash`, `SizeBytes`, `Seeders`, `Leechers`, `IndexerPriority`, `PublishedAt`), `SizeParser.Parse`, and the whole `Profiles/` decision stack. **Do not modify any Stage 0a file.** 180 tests pass; every task here must leave them passing.

## Deferred to Stage 0b-2, deliberately

The TorrentBay and LimeTorrents scrapers, the FlareSolverr client, and bencode payload verification. They are site-specific and fragile in a way RSS and Torznab are not, and separating them means the durable half stays landed when a site changes its markup. Fixtures for all of them are already in `tests/fixtures/`.

## One thing the real fixtures revealed

`tests/fixtures/scnsrc-feed.xml` items carry **no `<enclosure>` and no magnet link** — a scene feed announces that a release exists, it does not offer it for download. A tracker's RSS feed does the opposite. So an RSS item is one of two kinds, and the difference is load-bearing: a discovery-only item names a release to go and search for, while a download-capable item can be grabbed directly. `ReleaseInfo` already models this — `MagnetUri` and `DownloadUrl` are both nullable — and Task 3 makes the distinction explicit rather than silently emitting unusable candidates.

---

## File Structure

```
src/NoMercy.Plugin.TorrentDownloader.Core/
├── Indexers/
│   ├── SearchQuery.cs            what to search for
│   ├── IIndexer.cs               the port
│   ├── IndexerException.cs       one exception type callers can catch
│   ├── IClock.cs                 injected time
│   ├── SystemClock.cs            the one impl that reads the real clock
│   ├── Parsing/
│   │   ├── RssItem.cs            one parsed feed item
│   │   ├── RssFeedParser.cs      xml string -> RssItem[]      (pure)
│   │   └── TorznabResultParser.cs xml string -> ReleaseInfo[] (pure)
│   ├── RssIndexer.cs             IIndexer over a feed URL
│   ├── TorznabIndexer.cs         IIndexer over a Torznab endpoint
│   ├── IndexerPacer.cs           per-indexer interval + concurrency + breaker
│   └── IndexerAggregator.cs      fan-out, pace, degrade, merge
└── (Stage 0a namespaces untouched)

tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/
├── Indexers/
│   ├── RssFeedParserTests.cs
│   ├── TorznabResultParserTests.cs
│   ├── RssIndexerTests.cs
│   ├── TorznabIndexerTests.cs
│   ├── IndexerPacerTests.cs
│   └── IndexerAggregatorTests.cs
└── TestSupport/
    ├── FakeClock.cs
    ├── StubHttpMessageHandler.cs
    └── Fixtures.cs               loads tests/fixtures/* by name
```

---

## Task 1: Contract, clock, and fixture loading

**Files:**
- Create: `src/.../Indexers/SearchQuery.cs`, `IIndexer.cs`, `IndexerException.cs`, `IClock.cs`, `SystemClock.cs`
- Create: `tests/.../TestSupport/FakeClock.cs`, `StubHttpMessageHandler.cs`, `Fixtures.cs`
- Modify: `tests/.../NoMercy.Plugin.TorrentDownloader.Core.Tests.csproj` (copy fixtures to output)
- Test: `tests/.../Indexers/ContractTests.cs`

**Interfaces:**
- Produces: `SearchQuery`, `IIndexer`, `IndexerException`, `IClock`, `SystemClock`, and the three test helpers every later task uses.

- [ ] **Step 1: Write the failing test**

Create `tests/.../Indexers/ContractTests.cs`:

```csharp
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Indexers;

public class ContractTests
{
    private sealed class FakeIndexer : IIndexer
    {
        public string Name => "fake";
        public int Priority => 3;

        public Task<IReadOnlyList<ReleaseInfo>> SearchAsync(SearchQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ReleaseInfo>>([]);
    }

    [Fact]
    public async Task IIndexer_ExposesNamePriorityAndSearch()
    {
        IIndexer indexer = new FakeIndexer();

        indexer.Name.Should().Be("fake");
        indexer.Priority.Should().Be(3);
        (await indexer.SearchAsync(new SearchQuery("Silo"), CancellationToken.None))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void SearchQuery_CarriesTheEpisodeSlotWhenOneIsWanted()
    {
        SearchQuery query = new("Silo", new EpisodeSlot(3, 4));

        query.ShowName.Should().Be("Silo");
        query.Slot.Should().Be(new EpisodeSlot(3, 4));
        query.Text.Should().Be("Silo S03E04");
    }

    [Fact]
    public void SearchQuery_FallsBackToTheShowNameWhenNoSlotIsWanted()
    {
        new SearchQuery("Silo").Text.Should().Be("Silo");
    }

    [Fact]
    public void FakeClock_AdvancesOnlyWhenTold()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);

        clock.UtcNow.Should().Be(DateTimeOffset.UnixEpoch);
        clock.Advance(TimeSpan.FromSeconds(30));
        clock.UtcNow.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(30));
    }

    [Fact]
    public void Fixtures_LoadTheRealCapturedSceneFeed()
    {
        string xml = Fixtures.Text("scnsrc-feed.xml");

        xml.Should().Contain("<rss").And.Contain("The Kelly Clarkson Show");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter ContractTests`
Expected: FAIL — build error, none of these types exist.

- [ ] **Step 3: Write the contract types**

`src/.../Indexers/SearchQuery.cs`:

```csharp
using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

public record SearchQuery(string ShowName, EpisodeSlot? Slot = null)
{
    public string Text => Slot is EpisodeSlot slot ? $"{ShowName} {slot}" : ShowName;
}
```

`src/.../Indexers/IIndexer.cs`:

```csharp
using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

public interface IIndexer
{
    string Name { get; }
    int Priority { get; }
    Task<IReadOnlyList<ReleaseInfo>> SearchAsync(SearchQuery query, CancellationToken ct);
}
```

`src/.../Indexers/IndexerException.cs`:

```csharp
namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

public class IndexerException : Exception
{
    public IndexerException(string message)
        : base(message) { }

    public IndexerException(string message, Exception inner)
        : base(message, inner) { }
}
```

`src/.../Indexers/IClock.cs`:

```csharp
namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

public interface IClock
{
    DateTimeOffset UtcNow { get; }

    Task DelayAsync(TimeSpan duration, CancellationToken ct);
}
```

`src/.../Indexers/SystemClock.cs`:

```csharp
namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan duration, CancellationToken ct) => Task.Delay(duration, ct);
}
```

- [ ] **Step 4: Write the test helpers**

`tests/.../TestSupport/FakeClock.cs`:

```csharp
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

public sealed class FakeClock(DateTimeOffset start) : IClock
{
    private DateTimeOffset _now = start;

    public DateTimeOffset UtcNow => _now;

    public List<TimeSpan> Delays { get; } = [];

    public void Advance(TimeSpan duration) => _now = _now.Add(duration);

    public Task DelayAsync(TimeSpan duration, CancellationToken ct)
    {
        Delays.Add(duration);
        _now = _now.Add(duration);
        return Task.CompletedTask;
    }
}
```

`tests/.../TestSupport/StubHttpMessageHandler.cs`:

```csharp
using System.Net;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

public sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, HttpResponseMessage> respond
) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    public static StubHttpMessageHandler Returning(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });

    public static StubHttpMessageHandler Throwing(Exception error) =>
        new(_ => throw error);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(respond(request));
    }

    public HttpClient Client() => new(this);
}
```

`tests/.../TestSupport/Fixtures.cs`:

```csharp
namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

public static class Fixtures
{
    public static string Text(string name) => File.ReadAllText(Path(name));

    public static byte[] Bytes(string name) => File.ReadAllBytes(Path(name));

    private static string Path(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", name);
}
```

- [ ] **Step 5: Copy fixtures to the test output**

Add to `tests/.../NoMercy.Plugin.TorrentDownloader.Core.Tests.csproj`, inside a new `ItemGroup`:

```xml
    <ItemGroup>
        <None Include="..\..\tests\fixtures\**\*">
            <Link>fixtures\%(RecursiveDir)%(Filename)%(Extension)</Link>
            <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
        </None>
    </ItemGroup>
```

Note the fixtures live at the repository's `tests/fixtures/`, beside the test project rather than inside it, because Stage 0b-2 will share them.

- [ ] **Step 6: Run tests to verify they pass**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests`
Expected: PASS, 185 cases (180 existing + 5 new).

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "feat(core): add the indexer contract, injected clock and fixture loading"
```

---

## Task 2: RSS feed parsing, against the real capture

**Files:**
- Create: `src/.../Indexers/Parsing/RssItem.cs`, `src/.../Indexers/Parsing/RssFeedParser.cs`
- Test: `tests/.../Indexers/RssFeedParserTests.cs`

**Interfaces:**
- Consumes: nothing from Stage 0a.
- Produces: `RssItem(string Title, string? Link, string? Guid, DateTimeOffset? Published, IReadOnlyList<string> Categories, string? EnclosureUrl, long EnclosureLength, string? EnclosureType)` and `RssFeedParser.Parse(string xml) : IReadOnlyList<RssItem>`, throwing `IndexerException` on malformed XML.

**This parser is tested against the real captured feed, not a hand-written sample.** Stage 0a's defects came almost entirely from invented test data being too polite; `tests/fixtures/scnsrc-feed.xml` is 40 real items and does not flatter the parser.

Two things in the real capture that a hand-written sample would miss: an item carries **multiple `<category>` elements**, and the feed uses the **`dc:` namespace** for `creator`, so any element lookup that ignores namespaces will behave unpredictably.

- [ ] **Step 1: Write the failing test**

Create `tests/.../Indexers/RssFeedParserTests.cs`:

```csharp
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers.Parsing;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Indexers;

public class RssFeedParserTests
{
    private static IReadOnlyList<RssItem> RealFeed() =>
        RssFeedParser.Parse(Fixtures.Text("scnsrc-feed.xml"));

    [Fact]
    public void Parse_ReadsEveryItemFromTheRealCapture()
    {
        RealFeed().Should().HaveCount(40);
    }

    [Fact]
    public void Parse_ReadsTitleLinkGuidAndPublishedDate()
    {
        RssItem first = RealFeed()[0];

        first.Title.Should().Be(
            "The Kelly Clarkson Show 2026 07 22 Guest Host Andy Cohen 1080p WEB h264-DiRT"
        );
        first.Link.Should().StartWith("https://www.scnsrc.me/");
        first.Guid.Should().Be("https://www.scnsrc.me/?p=541034");
        first.Published.Should().Be(new DateTimeOffset(2026, 7, 24, 20, 5, 42, TimeSpan.Zero));
    }

    [Fact]
    public void Parse_ReadsAllCategoriesNotJustTheFirst()
    {
        RssItem multiCategory = RealFeed()
            .Single(item => item.Title == "Her Private Hell 2026 720p CAM H264-CinemaCity");

        multiCategory.Categories.Should().BeEquivalentTo(["Cam", "Movies", "P2P"]);
    }

    [Fact]
    public void Parse_LeavesEnclosureEmptyForADiscoveryOnlyFeed()
    {
        RealFeed().Should().OnlyContain(item => item.EnclosureUrl == null);
    }

    [Fact]
    public void Parse_ReadsAnEnclosureWhenTheFeedOffersOne()
    {
        string xml = """
            <rss version="2.0"><channel>
              <item>
                <title>Silo S03E04 1080p WEB H264-CAKES</title>
                <link>https://tracker.example/t/1</link>
                <enclosure url="https://tracker.example/t/1.torrent"
                           length="1503238553"
                           type="application/x-bittorrent" />
              </item>
            </channel></rss>
            """;

        RssItem item = RssFeedParser.Parse(xml).Single();

        item.EnclosureUrl.Should().Be("https://tracker.example/t/1.torrent");
        item.EnclosureLength.Should().Be(1503238553L);
        item.EnclosureType.Should().Be("application/x-bittorrent");
    }

    [Fact]
    public void Parse_SkipsAnItemWithNoTitle()
    {
        string xml = """
            <rss version="2.0"><channel>
              <item><link>https://x/1</link></item>
              <item><title>Silo S03E04 1080p</title></item>
            </channel></rss>
            """;

        RssFeedParser.Parse(xml).Should().ContainSingle();
    }

    [Fact]
    public void Parse_ThrowsIndexerExceptionOnMalformedXml()
    {
        Action act = () => RssFeedParser.Parse("<rss><channel><item>");

        act.Should().Throw<IndexerException>().WithMessage("*feed*");
    }

    [Fact]
    public void Parse_ThrowsIndexerExceptionOnAnEmptyBody()
    {
        Action act = () => RssFeedParser.Parse("");

        act.Should().Throw<IndexerException>();
    }

    [Fact]
    public void Parse_LeavesPublishedNullWhenTheDateIsUnparseable()
    {
        string xml = """
            <rss version="2.0"><channel>
              <item><title>Silo S03E04</title><pubDate>not a date</pubDate></item>
            </channel></rss>
            """;

        RssFeedParser.Parse(xml).Single().Published.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter RssFeedParserTests`
Expected: FAIL — `RssItem` and `RssFeedParser` do not exist.

- [ ] **Step 3: Write RssItem**

`src/.../Indexers/Parsing/RssItem.cs`:

```csharp
namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers.Parsing;

public record RssItem
{
    public required string Title { get; init; }
    public string? Link { get; init; }
    public string? Guid { get; init; }
    public DateTimeOffset? Published { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public string? EnclosureUrl { get; init; }
    public long EnclosureLength { get; init; }
    public string? EnclosureType { get; init; }
}
```

- [ ] **Step 4: Write RssFeedParser**

`src/.../Indexers/Parsing/RssFeedParser.cs`:

```csharp
using System.Globalization;
using System.Xml.Linq;

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers.Parsing;

public static class RssFeedParser
{
    public static IReadOnlyList<RssItem> Parse(string xml)
    {
        XDocument document;

        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException error)
        {
            throw new IndexerException($"malformed feed XML: {error.Message}", error);
        }

        List<RssItem> items = [];

        foreach (XElement element in document.Descendants("item"))
        {
            string title = (string?)element.Element("title") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(title))
                continue;

            XElement? enclosure = element.Element("enclosure");

            items.Add(
                new RssItem
                {
                    Title = title.Trim(),
                    Link = Trimmed(element.Element("link")),
                    Guid = Trimmed(element.Element("guid")),
                    Published = ParseDate(Trimmed(element.Element("pubDate"))),
                    Categories = element
                        .Elements("category")
                        .Select(category => ((string)category).Trim())
                        .Where(category => category.Length > 0)
                        .ToArray(),
                    EnclosureUrl = Trimmed(enclosure?.Attribute("url")),
                    EnclosureLength = ParseLength(Trimmed(enclosure?.Attribute("length"))),
                    EnclosureType = Trimmed(enclosure?.Attribute("type")),
                }
            );
        }

        return items;
    }

    private static string? Trimmed(XElement? element)
    {
        string? value = ((string?)element)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? Trimmed(XAttribute? attribute)
    {
        string? value = ((string?)attribute)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static DateTimeOffset? ParseDate(string? text) =>
        DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed
        )
            ? parsed
            : null;

    private static long ParseLength(string? text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long length)
            ? length
            : 0L;
}
```

`Descendants("item")` rather than `Element("channel").Elements("item")`: the capture nests items under `channel`, but not every feed does, and the elements this parser reads are unprefixed in both. The `dc:`-namespaced elements are deliberately not read — nothing here needs the creator.

- [ ] **Step 5: Run test to verify it passes**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter RssFeedParserTests`
Expected: PASS, 9 cases (suite total 194).

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat(core): parse RSS feeds against the real captured scene feed"
```

---

## Task 3: RssIndexer

**Files:**
- Create: `src/.../Indexers/RssIndexer.cs`
- Test: `tests/.../Indexers/RssIndexerTests.cs`

**Interfaces:**
- Consumes: `IIndexer`, `SearchQuery`, `IndexerException`, `RssFeedParser`, `ReleaseInfo`, `SizeParser`.
- Produces: `RssIndexer(string name, int priority, Uri feedUrl, HttpClient http, IReadOnlyList<string>? categories = null)` implementing `IIndexer`.

**The two-kinds distinction lives here.** An item with an enclosure or a magnet link becomes a grabbable `ReleaseInfo`. An item with neither is **discovery-only**: the plugin knows the release exists but must search an indexer to get it. Both are returned — dropping discovery-only items would blind the feed tier described in spec §7.2 — and the caller tells them apart by `MagnetUri` and `DownloadUrl` both being null.

An RSS feed is a *fixed* URL; it does not accept a search term. `SearchAsync` therefore fetches the whole feed and filters client-side by category. Matching a query against titles is the decision stack's job, not the indexer's.

- [ ] **Step 1: Write the failing test**

Create `tests/.../Indexers/RssIndexerTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Indexers;

public class RssIndexerTests
{
    private static RssIndexer Indexer(
        StubHttpMessageHandler handler,
        IReadOnlyList<string>? categories = null
    ) =>
        new("scnsrc", 5, new Uri("https://feed.example/rss"), handler.Client(), categories);

    [Fact]
    public async Task SearchAsync_ReturnsEveryItemFromTheRealCapture()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(
            Fixtures.Text("scnsrc-feed.xml")
        );

        IReadOnlyList<ReleaseInfo> results = await Indexer(handler)
            .SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        results.Should().HaveCount(40);
        results.Should().OnlyContain(release => release.IndexerName == "scnsrc");
        results.Should().OnlyContain(release => release.IndexerPriority == 5);
    }

    [Fact]
    public async Task SearchAsync_KeepsOnlyTheConfiguredCategories()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(
            Fixtures.Text("scnsrc-feed.xml")
        );

        IReadOnlyList<ReleaseInfo> results = await Indexer(handler, ["TV"])
            .SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        results.Should().HaveCount(21);
    }

    [Fact]
    public async Task SearchAsync_MarksSceneFeedItemsAsDiscoveryOnly()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(
            Fixtures.Text("scnsrc-feed.xml")
        );

        IReadOnlyList<ReleaseInfo> results = await Indexer(handler)
            .SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        results.Should().OnlyContain(release => release.MagnetUri == null && release.DownloadUrl == null);
    }

    [Fact]
    public async Task SearchAsync_ReadsAnEnclosureAsADownloadUrlAndSize()
    {
        string xml = """
            <rss version="2.0"><channel>
              <item>
                <title>Silo S03E04 1080p WEB H264-CAKES</title>
                <link>https://tracker.example/t/1</link>
                <enclosure url="https://tracker.example/t/1.torrent"
                           length="1503238553"
                           type="application/x-bittorrent" />
              </item>
            </channel></rss>
            """;

        ReleaseInfo release = (
            await Indexer(StubHttpMessageHandler.Returning(xml))
                .SearchAsync(new SearchQuery("Silo"), CancellationToken.None)
        ).Single();

        release.DownloadUrl.Should().Be("https://tracker.example/t/1.torrent");
        release.SizeBytes.Should().Be(1503238553L);
        release.DetailUrl.Should().Be("https://tracker.example/t/1");
    }

    [Fact]
    public async Task SearchAsync_ReadsAMagnetLinkAndItsInfoHash()
    {
        string xml = """
            <rss version="2.0"><channel>
              <item>
                <title>Silo S03E04 1080p WEB H264-CAKES</title>
                <link>magnet:?xt=urn:btih:ABCDEF0123456789ABCDEF0123456789ABCDEF01&amp;dn=Silo</link>
              </item>
            </channel></rss>
            """;

        ReleaseInfo release = (
            await Indexer(StubHttpMessageHandler.Returning(xml))
                .SearchAsync(new SearchQuery("Silo"), CancellationToken.None)
        ).Single();

        release.MagnetUri.Should().StartWith("magnet:?xt=urn:btih:");
        release.InfoHash.Should().Be("abcdef0123456789abcdef0123456789abcdef01");
        release.DetailUrl.Should().BeNull();
    }

    [Fact]
    public async Task SearchAsync_ThrowsIndexerExceptionOnAnErrorStatus()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(
            "nope",
            HttpStatusCode.ServiceUnavailable
        );

        Func<Task> act = () =>
            Indexer(handler).SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        await act.Should().ThrowAsync<IndexerException>().WithMessage("*503*");
    }

    [Fact]
    public async Task SearchAsync_ThrowsIndexerExceptionWhenTheRequestFails()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Throwing(
            new HttpRequestException("dns")
        );

        Func<Task> act = () =>
            Indexer(handler).SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        await act.Should().ThrowAsync<IndexerException>();
    }

    [Fact]
    public async Task SearchAsync_LetsCallerCancellationPropagate()
    {
        using CancellationTokenSource source = new();
        await source.CancelAsync();

        StubHttpMessageHandler handler = StubHttpMessageHandler.Throwing(
            new OperationCanceledException()
        );

        Func<Task> act = () =>
            Indexer(handler).SearchAsync(new SearchQuery("Silo"), source.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SearchAsync_WrapsATimeoutThatTheCallerDidNotRequest()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Throwing(
            new OperationCanceledException()
        );

        Func<Task> act = () =>
            Indexer(handler).SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        await act.Should().ThrowAsync<IndexerException>();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter RssIndexerTests`
Expected: FAIL — `RssIndexer` does not exist.

- [ ] **Step 3: Write the implementation**

`src/.../Indexers/RssIndexer.cs`:

```csharp
using System.Text.RegularExpressions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers.Parsing;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

public sealed partial class RssIndexer(
    string name,
    int priority,
    Uri feedUrl,
    HttpClient http,
    IReadOnlyList<string>? categories = null
) : IIndexer
{
    [GeneratedRegex(
        @"btih:([0-9a-f]{40}|[0-9a-z]{32})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex InfoHashPattern();

    public string Name => name;

    public int Priority => priority;

    public async Task<IReadOnlyList<ReleaseInfo>> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        string body = await FetchAsync(ct);

        return RssFeedParser
            .Parse(body)
            .Where(InConfiguredCategories)
            .Select(ToRelease)
            .ToArray();
    }

    private async Task<string> FetchAsync(CancellationToken ct)
    {
        HttpResponseMessage response;

        try
        {
            response = await http.GetAsync(feedUrl, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            throw new IndexerException($"{name}: feed request failed: {error.Message}", error);
        }

        if (!response.IsSuccessStatusCode)
            throw new IndexerException($"{name}: feed returned HTTP {(int)response.StatusCode}");

        // GetAsync defaults to HttpCompletionOption.ResponseContentRead, so the body is already
        // buffered when it returns and a transport failure surfaces inside the try above. This
        // call only decodes an in-memory buffer, which is why it needs no guard of its own.
        return await response.Content.ReadAsStringAsync(ct);
    }

    private bool InConfiguredCategories(RssItem item) =>
        categories is null
        || categories.Count == 0
        || item.Categories.Any(category => categories.Contains(category, StringComparer.OrdinalIgnoreCase));

    private ReleaseInfo ToRelease(RssItem item)
    {
        bool isMagnet = item.Link?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true;
        string? magnet = isMagnet ? item.Link : null;
        Match hash = InfoHashPattern().Match(magnet ?? string.Empty);

        return new ReleaseInfo
        {
            IndexerName = name,
            TorrentId = item.Guid ?? item.Link ?? item.Title,
            Title = item.Title,
            DetailUrl = isMagnet ? null : item.Link,
            MagnetUri = magnet,
            DownloadUrl = item.EnclosureUrl,
            InfoHash = hash.Success ? hash.Groups[1].Value.ToLowerInvariant() : null,
            SizeBytes = item.EnclosureLength,
            IndexerPriority = priority,
            PublishedAt = item.Published,
        };
    }
}
```

Seeders and leechers stay zero: a plain RSS feed does not report them. That matters downstream — a profile with a seeder floor rejects every RSS candidate — which is exactly why the feed tier's job is to *name* a release for the search tier to resolve, not to be grabbed from directly.

- [ ] **Step 4: Run test to verify it passes**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter RssIndexerTests`
Expected: PASS, 9 cases (suite total 203).

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(core): add RssIndexer reading discovery-only and grabbable feeds"
```

---

## Task 4: Torznab result parsing

**Files:**
- Create: `src/.../Indexers/Parsing/TorznabResultParser.cs`
- Test: `tests/.../Indexers/TorznabResultParserTests.cs`

**Interfaces:**
- Consumes: `ReleaseInfo`, `IndexerException`.
- Produces: `TorznabResultParser.Parse(string xml, string indexerName, int priority) : IReadOnlyList<ReleaseInfo>`.

Torznab is an RSS dialect: items carry `<torznab:attr name="..." value="..."/>` elements in the `http://torznab.com/schemas/2015/feed` namespace for seeders, peers, infohash and size. **The namespace is required** — reading `attr` unprefixed finds nothing on a real response.

An error response is a `<error code="..." description="..."/>` document rather than a feed, and must surface as an `IndexerException` rather than an empty result, or a misconfigured API key looks identical to "nothing found".

- [ ] **Step 1: Write the failing test**

Create `tests/.../Indexers/TorznabResultParserTests.cs`:

```csharp
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers.Parsing;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Indexers;

public class TorznabResultParserTests
{
    private const string Response = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
          <channel>
            <item>
              <title>Silo S03E04 1080p WEB H264-CAKES</title>
              <guid>https://indexer.example/details/1</guid>
              <comments>https://indexer.example/details/1</comments>
              <pubDate>Fri, 24 Jul 2026 20:05:42 +0000</pubDate>
              <size>1503238553</size>
              <link>https://indexer.example/download/1.torrent</link>
              <torznab:attr name="seeders" value="42" />
              <torznab:attr name="peers" value="50" />
              <torznab:attr name="infohash" value="ABCDEF0123456789ABCDEF0123456789ABCDEF01" />
            </item>
            <item>
              <title>Silo S03E05 720p WEB H264-CAKES</title>
              <guid>https://indexer.example/details/2</guid>
              <size>800000000</size>
              <link>magnet:?xt=urn:btih:1111111111111111111111111111111111111111&amp;dn=Silo</link>
              <torznab:attr name="seeders" value="7" />
              <torznab:attr name="peers" value="9" />
            </item>
          </channel>
        </rss>
        """;

    private static IReadOnlyList<ReleaseInfo> Parsed() =>
        TorznabResultParser.Parse(Response, "prowlarr", 9);

    [Fact]
    public void Parse_ReadsEveryItem()
    {
        Parsed().Should().HaveCount(2);
    }

    [Fact]
    public void Parse_ReadsSeedersFromTheNamespacedAttribute()
    {
        Parsed()[0].Seeders.Should().Be(42);
    }

    [Fact]
    public void Parse_DerivesLeechersBySubtractingSeedersFromPeers()
    {
        Parsed()[0].Leechers.Should().Be(8);
    }

    [Fact]
    public void Parse_LowercasesTheInfoHash()
    {
        Parsed()[0].InfoHash.Should().Be("abcdef0123456789abcdef0123456789abcdef01");
    }

    [Fact]
    public void Parse_ReadsSizeAndPublishedDate()
    {
        ReleaseInfo first = Parsed()[0];

        first.SizeBytes.Should().Be(1503238553L);
        first.PublishedAt.Should().Be(new DateTimeOffset(2026, 7, 24, 20, 5, 42, TimeSpan.Zero));
    }

    [Fact]
    public void Parse_TreatsAnHttpLinkAsADownloadUrl()
    {
        ReleaseInfo first = Parsed()[0];

        first.DownloadUrl.Should().Be("https://indexer.example/download/1.torrent");
        first.MagnetUri.Should().BeNull();
    }

    [Fact]
    public void Parse_TreatsAMagnetLinkAsAMagnetAndRecoversItsInfoHash()
    {
        ReleaseInfo second = Parsed()[1];

        second.MagnetUri.Should().StartWith("magnet:?xt=urn:btih:");
        second.DownloadUrl.Should().BeNull();
        second.InfoHash.Should().Be("1111111111111111111111111111111111111111");
    }

    [Fact]
    public void Parse_StampsTheIndexerNameAndPriority()
    {
        Parsed().Should().OnlyContain(release => release.IndexerName == "prowlarr");
        Parsed().Should().OnlyContain(release => release.IndexerPriority == 9);
    }

    [Fact]
    public void Parse_ReadsSizeFromTheAttributeWhenThereIsNoSizeElement()
    {
        string attrSize = """
            <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
              <channel><item>
                <title>Silo S03E04 1080p WEB H264-CAKES</title>
                <torznab:attr name="size" value="1503238553" />
              </item></channel>
            </rss>
            """;

        TorznabResultParser.Parse(attrSize, "x", 0).Single().SizeBytes.Should().Be(1503238553L);
    }

    [Fact]
    public void Parse_PrefersTheSizeElementWhenBothArePresent()
    {
        string both = """
            <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
              <channel><item>
                <title>Silo S03E04 1080p WEB H264-CAKES</title>
                <size>111</size>
                <torznab:attr name="size" value="222" />
              </item></channel>
            </rss>
            """;

        TorznabParserSize(both).Should().Be(111L);
    }

    private static long TorznabParserSize(string xml) =>
        TorznabResultParser.Parse(xml, "x", 0).Single().SizeBytes;

    [Fact]
    public void Parse_ThrowsIndexerExceptionOnAnErrorDocument()
    {
        string error = """
            <?xml version="1.0" encoding="UTF-8"?>
            <error code="100" description="Incorrect user credentials" />
            """;

        Action act = () => TorznabResultParser.Parse(error, "prowlarr", 9);

        act.Should()
            .Throw<IndexerException>()
            .WithMessage("*Incorrect user credentials*");
    }

    [Fact]
    public void Parse_ThrowsIndexerExceptionOnMalformedXml()
    {
        Action act = () => TorznabResultParser.Parse("<rss>", "prowlarr", 9);

        act.Should().Throw<IndexerException>();
    }

    [Fact]
    public void Parse_DefaultsMissingAttributesToZeroRatherThanThrowing()
    {
        string sparse = """
            <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
              <channel><item><title>Silo S03E04 1080p</title></item></channel>
            </rss>
            """;

        ReleaseInfo release = TorznabResultParser.Parse(sparse, "x", 0).Single();

        release.Seeders.Should().Be(0);
        release.Leechers.Should().Be(0);
        release.SizeBytes.Should().Be(0);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter TorznabResultParserTests`
Expected: FAIL — `TorznabResultParser` does not exist.

- [ ] **Step 3: Write the implementation**

`src/.../Indexers/Parsing/TorznabResultParser.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers.Parsing;

public static partial class TorznabResultParser
{
    private static readonly XNamespace Torznab = "http://torznab.com/schemas/2015/feed";

    [GeneratedRegex(
        @"btih:([0-9a-f]{40}|[0-9a-z]{32})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex InfoHashPattern();

    public static IReadOnlyList<ReleaseInfo> Parse(string xml, string indexerName, int priority)
    {
        XDocument document;

        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException error)
        {
            throw new IndexerException($"{indexerName}: malformed Torznab XML: {error.Message}", error);
        }

        if (document.Root?.Name.LocalName == "error")
            throw new IndexerException(
                $"{indexerName}: Torznab error {(string?)document.Root.Attribute("code")}: "
                    + (string?)document.Root.Attribute("description")
            );

        return document.Descendants("item").Select(item => ToRelease(item, indexerName, priority)).ToArray();
    }

    private static ReleaseInfo ToRelease(XElement item, string indexerName, int priority)
    {
        string? link = ((string?)item.Element("link"))?.Trim();
        bool isMagnet = link?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true;

        int seeders = Attr(item, "seeders");
        int peers = Attr(item, "peers");
        string? infoHash = AttrText(item, "infohash");

        if (infoHash is null && isMagnet)
        {
            Match match = InfoHashPattern().Match(link!);
            infoHash = match.Success ? match.Groups[1].Value : null;
        }

        return new ReleaseInfo
        {
            IndexerName = indexerName,
            TorrentId = ((string?)item.Element("guid"))?.Trim() ?? link ?? string.Empty,
            Title = ((string?)item.Element("title"))?.Trim() ?? string.Empty,
            DetailUrl = ((string?)item.Element("comments"))?.Trim(),
            MagnetUri = isMagnet ? link : null,
            DownloadUrl = isMagnet ? null : link,
            InfoHash = infoHash?.ToLowerInvariant(),
            SizeBytes = ParseSize(item),
            Seeders = seeders,
            Leechers = Math.Max(peers - seeders, 0),
            IndexerPriority = priority,
            PublishedAt = Date(item.Element("pubDate")),
        };
    }

    // Torznab permits size as either a <size> element or a torznab:attr, and an endpoint that
    // uses only the attr form would otherwise report every release as zero bytes — silently
    // wrong rather than visibly broken, since the size filter would still run against it.
    private static long ParseSize(XElement item)
    {
        long element = Long(item.Element("size"));
        if (element > 0L)
            return element;

        return long.TryParse(
            AttrText(item, "size"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out long attribute
        )
            ? attribute
            : 0L;
    }

    private static string? AttrText(XElement item, string name) =>
        item.Elements(Torznab + "attr")
            .FirstOrDefault(attr =>
                string.Equals((string?)attr.Attribute("name"), name, StringComparison.OrdinalIgnoreCase)
            )
            ?.Attribute("value")
            ?.Value;

    private static int Attr(XElement item, string name) =>
        int.TryParse(
            AttrText(item, name),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int value
        )
            ? value
            : 0;

    private static long Long(XElement? element) =>
        long.TryParse(
            (string?)element,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out long value
        )
            ? value
            : 0L;

    private static DateTimeOffset? Date(XElement? element) =>
        DateTimeOffset.TryParse(
            (string?)element,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset value
        )
            ? value
            : null;
}
```

**`Leechers` is `peers - seeders`, floored at zero.** Torznab reports `peers` as the total swarm including seeders, so subtracting is correct; the floor guards an indexer that reports them inconsistently.

- [ ] **Step 4: Run test to verify it passes**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter TorznabResultParserTests`
Expected: PASS, 13 cases (suite total 216).

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(core): parse Torznab results including namespaced attributes"
```

---

## Task 5: TorznabIndexer

**Files:**
- Create: `src/.../Indexers/TorznabIndexer.cs`
- Test: `tests/.../Indexers/TorznabIndexerTests.cs`

**Interfaces:**
- Consumes: `IIndexer`, `SearchQuery`, `TorznabResultParser`, `IndexerException`.
- Produces: `TorznabIndexer(string name, int priority, Uri baseUrl, string apiKey, HttpClient http, IReadOnlyList<int>? categories = null)` implementing `IIndexer`.

Unlike RSS, Torznab **accepts a search term**, so the query goes to the server and far less comes back. When the query carries an episode slot it uses the `tvsearch` function with `season`/`ep` parameters, which is what lets the indexer do the episode filtering server-side; without a slot it falls back to a plain `search`.

**The API key must never appear in an exception message or a log line.** Torznab passes it as a query parameter, so a naive "request to {url} failed" leaks the user's credential.

- [ ] **Step 1: Write the failing test**

Create `tests/.../Indexers/TorznabIndexerTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Indexers;

public class TorznabIndexerTests
{
    private const string Empty = """
        <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
          <channel />
        </rss>
        """;

    private static TorznabIndexer Indexer(
        StubHttpMessageHandler handler,
        IReadOnlyList<int>? categories = null
    ) =>
        new(
            "prowlarr",
            9,
            new Uri("https://indexer.example/api"),
            "SECRETKEY",
            handler.Client(),
            categories
        );

    [Fact]
    public async Task SearchAsync_UsesTvSearchWithSeasonAndEpisodeWhenASlotIsWanted()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(Empty);

        await Indexer(handler)
            .SearchAsync(new SearchQuery("Silo", new EpisodeSlot(3, 4)), CancellationToken.None);

        string url = handler.Requests.Single().ToString();
        url.Should().Contain("t=tvsearch");
        url.Should().Contain("q=Silo");
        url.Should().Contain("season=3");
        url.Should().Contain("ep=4");
    }

    [Fact]
    public async Task SearchAsync_FallsBackToAPlainSearchWithoutASlot()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(Empty);

        await Indexer(handler).SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        string url = handler.Requests.Single().ToString();
        url.Should().Contain("t=search");
        url.Should().NotContain("season=");
        url.Should().NotContain("ep=");
    }

    [Fact]
    public async Task SearchAsync_SendsTheApiKeyAndConfiguredCategories()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(Empty);

        await Indexer(handler, [5030, 5040])
            .SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        string url = handler.Requests.Single().ToString();
        url.Should().Contain("apikey=SECRETKEY");
        url.Should().Contain("cat=5030,5040");
    }

    [Fact]
    public async Task SearchAsync_EscapesTheQueryText()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(Empty);

        await Indexer(handler)
            .SearchAsync(new SearchQuery("It's Always Sunny"), CancellationToken.None);

        // AbsoluteUri, not ToString(): ToString() returns a display form that unescapes %20 back
        // to a literal space, so asserting on it would fail against correctly escaped output.
        handler.Requests.Single().AbsoluteUri.Should().NotContain(" ");
        handler.Requests.Single().AbsoluteUri.Should().Contain("%20");
    }

    [Fact]
    public async Task SearchAsync_NeverPutsTheApiKeyInAnExceptionMessage()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(
            "nope",
            HttpStatusCode.Unauthorized
        );

        Func<Task> act = () =>
            Indexer(handler).SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        (await act.Should().ThrowAsync<IndexerException>()).And.Message.Should()
            .NotContain("SECRETKEY");
    }

    [Fact]
    public async Task SearchAsync_ThrowsIndexerExceptionWhenTheRequestFails()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Throwing(
            new HttpRequestException("dns")
        );

        Func<Task> act = () =>
            Indexer(handler).SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        (await act.Should().ThrowAsync<IndexerException>()).And.Message.Should()
            .NotContain("SECRETKEY");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter TorznabIndexerTests`
Expected: FAIL — `TorznabIndexer` does not exist.

- [ ] **Step 3: Write the implementation**

`src/.../Indexers/TorznabIndexer.cs`:

```csharp
using System.Globalization;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers.Parsing;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

public sealed class TorznabIndexer(
    string name,
    int priority,
    Uri baseUrl,
    string apiKey,
    HttpClient http,
    IReadOnlyList<int>? categories = null
) : IIndexer
{
    public string Name => name;

    public int Priority => priority;

    public async Task<IReadOnlyList<ReleaseInfo>> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        Uri url = BuildUrl(query);
        HttpResponseMessage response;

        try
        {
            response = await http.GetAsync(url, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            throw new IndexerException($"{name}: search request failed: {error.Message}", error);
        }

        if (!response.IsSuccessStatusCode)
            throw new IndexerException($"{name}: search returned HTTP {(int)response.StatusCode}");

        // Buffered by GetAsync's default completion option — see RssIndexer.FetchAsync.
        string body = await response.Content.ReadAsStringAsync(ct);
        return TorznabResultParser.Parse(body, name, priority);
    }

    private Uri BuildUrl(SearchQuery query)
    {
        List<string> parameters =
        [
            $"t={(query.Slot is null ? "search" : "tvsearch")}",
            $"apikey={Uri.EscapeDataString(apiKey)}",
            $"q={Uri.EscapeDataString(query.ShowName)}",
        ];

        if (query.Slot is EpisodeSlot slot)
        {
            parameters.Add($"season={slot.Season.ToString(CultureInfo.InvariantCulture)}");
            parameters.Add($"ep={slot.Episode.ToString(CultureInfo.InvariantCulture)}");
        }

        if (categories is { Count: > 0 })
            parameters.Add(
                "cat="
                    + string.Join(",", categories.Select(c => c.ToString(CultureInfo.InvariantCulture)))
            );

        return new Uri($"{baseUrl.ToString().TrimEnd('/')}?{string.Join("&", parameters)}");
    }
}
```

The exception messages carry only the indexer name and the status or transport error — never the URL, because the URL contains the API key.

- [ ] **Step 4: Run test to verify it passes**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter TorznabIndexerTests`
Expected: PASS, 6 cases (suite total 222).

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(core): add TorznabIndexer with server-side episode filtering"
```

---

## Task 6: IndexerPacer — interval, concurrency, backoff, breaker

**Files:**
- Create: `src/.../Indexers/IndexerPacer.cs`
- Test: `tests/.../Indexers/IndexerPacerTests.cs`

**Interfaces:**
- Consumes: `IClock`, `IndexerException`.
- Produces: `IndexerPacer(IClock clock, TimeSpan minimumInterval, int maxConcurrency, int failureThreshold, TimeSpan cooldown)` with `Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)`, `bool IsParked`, and `TimeSpan? ParkedUntil`.

**This is the component that stops the plugin getting an account banned.** Spec §9.1: a six-hour backfill queries every enabled indexer once per wanted episode, which for a library of any size is precisely the access pattern that triggers a ban.

Four behaviours, all driven by the injected clock so they are testable without sleeping:

- a **minimum interval** between requests to one indexer;
- a **concurrency cap** so a fan-out cannot issue them all at once;
- **exponential backoff** on `429` and `503`, signalled by the work throwing an `IndexerException` whose message contains that status;
- a **circuit breaker** that parks an indexer for a cooldown after consecutive failures, and closes again after a success.

A parked indexer throws immediately rather than issuing a request — the aggregator treats that exactly like any other failure, so coverage degrades instead of the cycle stopping.

- [ ] **Step 1: Write the failing test**

Create `tests/.../Indexers/IndexerPacerTests.cs`:

```csharp
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Indexers;

public class IndexerPacerTests
{
    private static IndexerPacer Pacer(FakeClock clock) =>
        new(clock, TimeSpan.FromSeconds(2), maxConcurrency: 2, failureThreshold: 3, cooldown: TimeSpan.FromMinutes(5));

    [Fact]
    public async Task RunAsync_DoesNotDelayTheFirstCall()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);

        await Pacer(clock).RunAsync(_ => Task.FromResult(1), CancellationToken.None);

        clock.Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_WaitsTheMinimumIntervalBetweenCalls()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = Pacer(clock);

        await pacer.RunAsync(_ => Task.FromResult(1), CancellationToken.None);
        await pacer.RunAsync(_ => Task.FromResult(2), CancellationToken.None);

        clock.Delays.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RunAsync_DoesNotWaitWhenEnoughTimeAlreadyPassed()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = Pacer(clock);

        await pacer.RunAsync(_ => Task.FromResult(1), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(30));
        await pacer.RunAsync(_ => Task.FromResult(2), CancellationToken.None);

        clock.Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_BacksOffExponentiallyOnRateLimitResponses()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = new(clock, TimeSpan.Zero, 2, failureThreshold: 99, cooldown: TimeSpan.FromMinutes(5));

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Func<Task> act = () =>
                pacer.RunAsync<int>(
                    _ => throw new IndexerException("x: search returned HTTP 429"),
                    CancellationToken.None
                );
            await act.Should().ThrowAsync<IndexerException>();
        }

        clock.Delays.Should().HaveCountGreaterThan(1);
        clock.Delays[^1].Should().BeGreaterThan(clock.Delays[0]);
    }

    [Fact]
    public async Task RunAsync_ParksTheIndexerAfterConsecutiveFailures()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = Pacer(clock);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Func<Task> act = () =>
                pacer.RunAsync<int>(_ => throw new IndexerException("boom"), CancellationToken.None);
            await act.Should().ThrowAsync<IndexerException>();
        }

        pacer.IsParked.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_ThrowsWithoutCallingTheWorkWhileParked()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = Pacer(clock);
        bool called = false;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Func<Task> fail = () =>
                pacer.RunAsync<int>(_ => throw new IndexerException("boom"), CancellationToken.None);
            await fail.Should().ThrowAsync<IndexerException>();
        }

        Func<Task> act = () =>
            pacer.RunAsync<int>(
                _ =>
                {
                    called = true;
                    return Task.FromResult(1);
                },
                CancellationToken.None
            );

        (await act.Should().ThrowAsync<IndexerException>()).And.Message.Should().Contain("parked");
        called.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_UnparksAfterTheCooldownElapses()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = Pacer(clock);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Func<Task> fail = () =>
                pacer.RunAsync<int>(_ => throw new IndexerException("boom"), CancellationToken.None);
            await fail.Should().ThrowAsync<IndexerException>();
        }

        clock.Advance(TimeSpan.FromMinutes(6));

        int result = await pacer.RunAsync(_ => Task.FromResult(7), CancellationToken.None);

        result.Should().Be(7);
        pacer.IsParked.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_ResetsTheFailureCountOnSuccess()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = Pacer(clock);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            Func<Task> fail = () =>
                pacer.RunAsync<int>(_ => throw new IndexerException("boom"), CancellationToken.None);
            await fail.Should().ThrowAsync<IndexerException>();
        }

        await pacer.RunAsync(_ => Task.FromResult(1), CancellationToken.None);

        Func<Task> once = () =>
            pacer.RunAsync<int>(_ => throw new IndexerException("boom"), CancellationToken.None);
        await once.Should().ThrowAsync<IndexerException>();

        pacer.IsParked.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_NeverRunsMoreThanTheConcurrencyCapAtOnce()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = new(clock, TimeSpan.Zero, maxConcurrency: 2, failureThreshold: 99, cooldown: TimeSpan.FromMinutes(5));
        int running = 0;
        int peak = 0;

        async Task<int> Work(CancellationToken ct)
        {
            int now = Interlocked.Increment(ref running);
            peak = Math.Max(peak, now);
            await Task.Yield();
            Interlocked.Decrement(ref running);
            return now;
        }

        await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => pacer.RunAsync(Work, CancellationToken.None))
        );

        peak.Should().BeLessThanOrEqualTo(2);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter IndexerPacerTests`
Expected: FAIL — `IndexerPacer` does not exist.

- [ ] **Step 3: Write the implementation**

`src/.../Indexers/IndexerPacer.cs`:

```csharp
namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

public sealed class IndexerPacer(
    IClock clock,
    TimeSpan minimumInterval,
    int maxConcurrency,
    int failureThreshold,
    TimeSpan cooldown
) : IDisposable
{
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _slots = new(maxConcurrency, maxConcurrency);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DateTimeOffset _lastStarted = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private int _rateLimitHits;
    private DateTimeOffset? _parkedUntil;

    public bool IsParked => _parkedUntil is DateTimeOffset until && clock.UtcNow < until;

    public TimeSpan? ParkedUntil =>
        _parkedUntil is DateTimeOffset until && clock.UtcNow < until ? until - clock.UtcNow : null;

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        if (IsParked)
            throw new IndexerException(
                $"indexer is parked for another {ParkedUntil!.Value.TotalSeconds:F0}s after repeated failures"
            );

        await _slots.WaitAsync(ct);

        try
        {
            await WaitForIntervalAsync(ct);
            T result = await work(ct);
            OnSuccess();
            return result;
        }
        catch (IndexerException error)
        {
            await OnFailureAsync(error, ct);
            throw;
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task WaitForIntervalAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);

        try
        {
            TimeSpan since = clock.UtcNow - _lastStarted;
            if (since < minimumInterval)
                await clock.DelayAsync(minimumInterval - since, ct);

            _lastStarted = clock.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnSuccess()
    {
        _consecutiveFailures = 0;
        _rateLimitHits = 0;
        _parkedUntil = null;
    }

    private async Task OnFailureAsync(IndexerException error, CancellationToken ct)
    {
        _consecutiveFailures++;

        if (IsRateLimited(error))
        {
            _rateLimitHits++;
            TimeSpan backoff = BaseBackoff * Math.Pow(2, _rateLimitHits - 1);
            await clock.DelayAsync(backoff < MaxBackoff ? backoff : MaxBackoff, ct);
        }

        if (_consecutiveFailures >= failureThreshold)
            _parkedUntil = clock.UtcNow + cooldown;
    }

    private static bool IsRateLimited(IndexerException error) =>
        error.Message.Contains("429", StringComparison.Ordinal)
        || error.Message.Contains("503", StringComparison.Ordinal);

    public void Dispose()
    {
        _slots.Dispose();
        _gate.Dispose();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter IndexerPacerTests`
Expected: PASS, 9 cases (suite total 231).

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(core): pace indexer requests with backoff and a circuit breaker"
```

---

## Task 7: IndexerAggregator

**Files:**
- Create: `src/.../Indexers/IndexerAggregator.cs`
- Test: `tests/.../Indexers/IndexerAggregatorTests.cs`

**Interfaces:**
- Consumes: `IIndexer`, `IndexerPacer`, `SearchQuery`, `ReleaseInfo`, `TitleMatcher.Normalize`.
- Produces: `IndexerAggregator(IReadOnlyList<PacedIndexer> indexers, Action<string>? log = null)` with `Task<AggregateResult> SearchAsync(SearchQuery query, CancellationToken ct)`; records `PacedIndexer(IIndexer Indexer, IndexerPacer Pacer)` and `AggregateResult(IReadOnlyList<ReleaseInfo> Releases, IReadOnlyList<IndexerFailure> Failures)`; record `IndexerFailure(string IndexerName, string Reason)`.

**One indexer failing must never stop a cycle** (spec §9.1). Failures are collected and returned alongside the results so the panel can show which indexers are degraded, rather than being swallowed into a log nobody reads.

**Deduplication runs on infohash first, then normalised title.** Infohash is the reliable identity; a title is the fallback when an indexer doesn't report one. When two indexers return the same release, the one from the **higher-priority** indexer wins, so a trusted tracker's copy — with its seeder counts and download URL — survives.

- [ ] **Step 1: Write the failing test**

Create `tests/.../Indexers/IndexerAggregatorTests.cs`:

```csharp
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Indexers;

public class IndexerAggregatorTests
{
    private sealed class StubIndexer(string name, int priority, params ReleaseInfo[] results) : IIndexer
    {
        public string Name => name;
        public int Priority => priority;
        public int Calls { get; private set; }

        public Task<IReadOnlyList<ReleaseInfo>> SearchAsync(SearchQuery query, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<ReleaseInfo>>(results);
        }
    }

    private sealed class FailingIndexer(string name) : IIndexer
    {
        public string Name => name;
        public int Priority => 0;

        public Task<IReadOnlyList<ReleaseInfo>> SearchAsync(SearchQuery query, CancellationToken ct) =>
            throw new IndexerException($"{name}: search returned HTTP 500");
    }

    private static ReleaseInfo Release(string title, string indexer, int priority, string? hash = null, int seeders = 10) =>
        new()
        {
            IndexerName = indexer,
            TorrentId = title + indexer,
            Title = title,
            InfoHash = hash,
            Seeders = seeders,
            IndexerPriority = priority,
        };

    private static PacedIndexer Paced(IIndexer indexer, FakeClock clock) =>
        new(indexer, new IndexerPacer(clock, TimeSpan.Zero, 4, 99, TimeSpan.FromMinutes(5)));

    [Fact]
    public async Task SearchAsync_MergesResultsFromEveryIndexer()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerAggregator aggregator = new(
            [
                Paced(new StubIndexer("a", 1, Release("Silo S03E04 1080p", "a", 1, "aaa")), clock),
                Paced(new StubIndexer("b", 2, Release("Silo S03E05 1080p", "b", 2, "bbb")), clock),
            ]
        );

        AggregateResult result = await aggregator.SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        result.Releases.Should().HaveCount(2);
        result.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_KeepsGoingWhenOneIndexerFails()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerAggregator aggregator = new(
            [
                Paced(new FailingIndexer("broken"), clock),
                Paced(new StubIndexer("good", 1, Release("Silo S03E04 1080p", "good", 1, "aaa")), clock),
            ]
        );

        AggregateResult result = await aggregator.SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        result.Releases.Should().ContainSingle();
        result.Failures.Should().ContainSingle();
        result.Failures[0].IndexerName.Should().Be("broken");
        result.Failures[0].Reason.Should().Contain("500");
    }

    [Fact]
    public async Task SearchAsync_ReturnsNoReleasesAndAllFailuresWhenEveryIndexerFails()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerAggregator aggregator = new(
            [Paced(new FailingIndexer("x"), clock), Paced(new FailingIndexer("y"), clock)]
        );

        AggregateResult result = await aggregator.SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        result.Releases.Should().BeEmpty();
        result.Failures.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchAsync_DeduplicatesOnInfoHashKeepingTheHigherPriorityIndexer()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerAggregator aggregator = new(
            [
                Paced(new StubIndexer("low", 1, Release("Silo S03E04 1080p", "low", 1, "SAMEHASH", seeders: 5)), clock),
                Paced(new StubIndexer("high", 9, Release("Silo S03E04 1080p", "high", 9, "samehash", seeders: 50)), clock),
            ]
        );

        AggregateResult result = await aggregator.SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        result.Releases.Should().ContainSingle();
        result.Releases[0].IndexerName.Should().Be("high");
    }

    [Fact]
    public async Task SearchAsync_DeduplicatesOnNormalisedTitleWhenNoInfoHashIsReported()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerAggregator aggregator = new(
            [
                Paced(new StubIndexer("low", 1, Release("Silo.S03E04.1080p.WEB.H264-CAKES", "low", 1)), clock),
                Paced(new StubIndexer("high", 9, Release("Silo S03E04 1080p WEB H264 CAKES", "high", 9)), clock),
            ]
        );

        AggregateResult result = await aggregator.SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        result.Releases.Should().ContainSingle();
        result.Releases[0].IndexerName.Should().Be("high");
    }

    [Fact]
    public async Task SearchAsync_ReportsAParkedIndexerAsAFailureWithoutCallingIt()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        StubIndexer stub = new("parked", 1, Release("Silo S03E04 1080p", "parked", 1, "aaa"));
        IndexerPacer pacer = new(clock, TimeSpan.Zero, 4, failureThreshold: 1, cooldown: TimeSpan.FromMinutes(5));

        Func<Task> trip = () =>
            pacer.RunAsync<int>(_ => throw new IndexerException("boom"), CancellationToken.None);
        await trip.Should().ThrowAsync<IndexerException>();

        IndexerAggregator aggregator = new([new PacedIndexer(stub, pacer)]);

        AggregateResult result = await aggregator.SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        result.Releases.Should().BeEmpty();
        result.Failures.Should().ContainSingle().Which.Reason.Should().Contain("parked");
        stub.Calls.Should().Be(0);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyWithoutFailuresWhenNoIndexersAreConfigured()
    {
        AggregateResult result = await new IndexerAggregator([])
            .SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        result.Releases.Should().BeEmpty();
        result.Failures.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter IndexerAggregatorTests`
Expected: FAIL — `IndexerAggregator`, `PacedIndexer` and `AggregateResult` do not exist.

- [ ] **Step 3: Write the implementation**

`src/.../Indexers/IndexerAggregator.cs`:

```csharp
using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

public record PacedIndexer(IIndexer Indexer, IndexerPacer Pacer);

public record IndexerFailure(string IndexerName, string Reason);

public record AggregateResult(
    IReadOnlyList<ReleaseInfo> Releases,
    IReadOnlyList<IndexerFailure> Failures
);

public sealed class IndexerAggregator(IReadOnlyList<PacedIndexer> indexers, Action<string>? log = null)
{
    public async Task<AggregateResult> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        List<ReleaseInfo>[] harvested = new List<ReleaseInfo>[indexers.Count];
        List<IndexerFailure> failures = [];

        await Task.WhenAll(
            indexers.Select(async (paced, index) =>
            {
                try
                {
                    IReadOnlyList<ReleaseInfo> found = await paced.Pacer.RunAsync(
                        token => paced.Indexer.SearchAsync(query, token),
                        ct
                    );
                    harvested[index] = [.. found];
                }
                catch (IndexerException error)
                {
                    harvested[index] = [];
                    lock (failures)
                    {
                        failures.Add(new IndexerFailure(paced.Indexer.Name, error.Message));
                    }
                    log?.Invoke($"{paced.Indexer.Name}: {error.Message}");
                }
            })
        );

        return new AggregateResult(Deduplicate(harvested), failures);
    }

    private static IReadOnlyList<ReleaseInfo> Deduplicate(IEnumerable<List<ReleaseInfo>> harvested)
    {
        Dictionary<string, ReleaseInfo> best = [];

        foreach (ReleaseInfo release in harvested.SelectMany(list => list))
        {
            string key = release.InfoHash is string hash
                ? "h:" + hash.ToLowerInvariant()
                : "t:" + TitleMatcher.Normalize(release.Title);

            if (
                !best.TryGetValue(key, out ReleaseInfo? existing)
                || release.IndexerPriority > existing.IndexerPriority
            )
                best[key] = release;
        }

        return [.. best.Values];
    }
}
```

`Task.WhenAll` over an index-addressed array rather than accumulating into a shared list: the per-indexer results are written to distinct slots, so only the failure list needs a lock.

- [ ] **Step 4: Run test to verify it passes**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests --filter IndexerAggregatorTests`
Expected: PASS, 7 cases (suite total 238).

- [ ] **Step 5: Run the whole suite and a Release build**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests`
Expected: PASS, 238 cases.

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" build nomercy-torrent-plugin.sln -c Release`
Expected: 0 warnings, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat(core): aggregate indexers with dedupe and graceful degradation"
```

---

## Plan Self-Review

**Spec coverage.** §9.1's `IIndexer` shape is Task 1; `RssIndexer` is Tasks 2–3; `TorznabIndexer` is Tasks 4–5; the aggregator's fan-out, merge, dedupe and failure survival are Task 7; the rate limiting, backoff and circuit breaker §9.1 requires are Task 6. §7.2's feed tier is served by `RssIndexer` returning discovery-only items rather than dropping them.

**Deliberately deferred to Stage 0b-2, and stated in the plan header:** `TorrentBayIndexer`, `LimeTorrentsIndexer`, the FlareSolverr client, and bencode payload verification. Their fixtures are already committed.

**Placeholder scan.** No "TBD", no "add error handling", no "similar to Task N". Every code step carries the full file.

**Type consistency.** `IIndexer.SearchAsync` returns `IReadOnlyList<ReleaseInfo>` in Tasks 1, 3, 5 and is consumed as such in Task 7. `IClock` is defined in Task 1 and consumed by `IndexerPacer` in Task 6 and `FakeClock` in Task 1. `IndexerException` is the single exception type thrown by every parser and client and caught in exactly one place, the aggregator. `TitleMatcher.Normalize` is Stage 0a's and is used only as a dedupe fallback here.

**Two things an implementer should watch.**

`Deduplicate` uses `TitleMatcher.Normalize` as a fallback key. Stage 0a's final review flagged that `Normalize` is drifting toward being a persistence format with several callers pulling at it; this adds a fourth. It is the right function for the job today, but if Stage 0c persists dedupe keys, that seam needs splitting first.

Task 6's backoff test asserts the delays grow rather than asserting exact values, because the exact sequence depends on the base and cap constants. If an implementer changes those constants the test still passes — that is deliberate, since the property under test is "backoff is exponential", not "backoff is 2s then 4s".

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-29-stage-0b-indexers.md`. Two execution options:

1. **Subagent-Driven (recommended)** — a fresh subagent per task, review between tasks, as Stage 0a was executed.
2. **Inline Execution** — execute tasks in this session with checkpoints.

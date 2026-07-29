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

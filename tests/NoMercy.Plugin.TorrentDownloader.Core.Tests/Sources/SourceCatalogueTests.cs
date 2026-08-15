using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Sources;

public class SourceCatalogueTests
{
    /// <remarks>
    /// A site whose address has moved can be corrected without waiting for a
    /// release. Two entries under one name would have the plugin asking the old
    /// address as well, which is the thing the owner was trying to stop.
    /// </remarks>
    [Fact]
    public void AnOwnerSourceWithAShippedNameReplacesIt()
    {
        SourceCatalogue catalogue = SourceCatalogue.Build(
            [Shipped("1337x", "https://old.example/{query}")],
            [Shipped("1337x", "https://new.example/{query}")],
            []);

        SourceDefinition source = Assert.Single(catalogue.All);
        Assert.Equal("https://new.example/{query}", source.Url);
    }

    /// <remarks>
    /// Dropped outright rather than kept and skipped: a source that will never
    /// be asked has no business in a list of sources that will be, and one left
    /// in is one a later change can quietly start asking again.
    /// </remarks>
    [Fact]
    public void AShippedSourceTheOwnerSwitchedOffIsDropped()
    {
        SourceCatalogue catalogue = SourceCatalogue.Build(
            [Shipped("1337x"), Shipped("Nyaa")],
            [],
            ["1337x"]);

        Assert.Equal(["Nyaa"], catalogue.All.Select(source => source.Name));
    }

    [Fact]
    public void SwitchingOffIsNotCaseSensitive()
    {
        SourceCatalogue catalogue = SourceCatalogue.Build([Shipped("Nyaa")], [], ["nyaa"]);

        Assert.Empty(catalogue.All);
    }

    /// <remarks>
    /// A source with <c>enabled</c> false is in the catalogue and is never
    /// asked, so the Sources page can say it exists and is off — which is a
    /// different thing from a source the owner has removed.
    /// </remarks>
    [Fact]
    public void ASourceThatIsOffIsListedButNeverAsked()
    {
        SourceCatalogue catalogue = SourceCatalogue.Build(
            [Shipped("YTS") with { Enabled = false }, Shipped("Nyaa")],
            [],
            []);

        Assert.Equal(2, catalogue.All.Count);
        Assert.Equal(["Nyaa"], catalogue.Enabled.Select(source => source.Name));
        Assert.DoesNotContain("yts.gg", catalogue.Hosts);
    }

    /// <remarks>
    /// Asking a source the wrong question is how 0.3.4 made forty identical
    /// requests a cycle: a feed answers any question with the newest N posts.
    /// </remarks>
    [Fact]
    public void EachRoleGetsOnlyTheSourcesThatCanAnswerIt()
    {
        SourceCatalogue catalogue = SourceCatalogue.Build(
            [
                Shipped("PreDB", "https://predb.me/?rss=1", "rss") with
                {
                    SearchUrl = "https://predb.me/?search={query}&rss=1",
                },
                Shipped("SceneSource", "https://www.scnsrc.me/feed/", "rss"),
                Shipped("1337x", "https://www.1337x.to/search/{query}/1/", "site"),
            ],
            [],
            []);

        Assert.Equal(["PreDB", "SceneSource"], catalogue.For(SourceRole.Feed).Select(source => source.Name));
        Assert.Equal(["PreDB"], catalogue.For(SourceRole.Names).Select(source => source.Name));
        Assert.Equal(["1337x"], catalogue.For(SourceRole.Indexer).Select(source => source.Name));
    }

    /// <remarks>
    /// Both addresses. The search host is the half that gets forgotten, and
    /// forgetting it is what left 0.3.4 requesting permission for no host at
    /// all on a default install.
    /// </remarks>
    [Fact]
    public void EveryHostOfEverySourceIsCountedIncludingSearchAddresses()
    {
        SourceCatalogue catalogue = SourceCatalogue.Build(
            [
                Shipped("EZTV", "https://eztvx.to/search/{query}", "site"),
                Shipped("EZTV latest", "https://eztv.re/api/get-torrents?limit=100", "eztv-api") with
                {
                    SearchUrl = "https://api.eztv.re/search/{query}",
                },
            ],
            [],
            []);

        Assert.Equal(
            ["eztvx.to", "eztv.re", "api.eztv.re"],
            catalogue.Hosts);
    }

    private static SourceDefinition Shipped(
        string name,
        string url = "https://example.test/{query}",
        string kind = "site")
    {
        return new(name, kind, url);
    }
}

public class SourceDefinitionTests
{
    /// <remarks>
    /// Decided from the kind and whether there is a search address, and by
    /// nothing else. Every row here is one of the seventeen in
    /// docs/05-sources.md § The shipped catalogue.
    /// </remarks>
    [Theory]
    [InlineData("rss", true, SourceRole.Feed | SourceRole.Names)]
    [InlineData("rss", false, SourceRole.Feed)]
    [InlineData("eztv-api", false, SourceRole.Feed)]
    [InlineData("srrdb", false, SourceRole.Names)]
    [InlineData("apibay", false, SourceRole.Indexer)]
    [InlineData("site", false, SourceRole.Indexer)]
    [InlineData("torrent-rss", false, SourceRole.Indexer)]
    [InlineData("yts", false, SourceRole.Indexer)]
    [InlineData("torznab", false, SourceRole.Indexer)]
    public void TheRoleComesFromTheKindAndTheSearchAddress(string kind, bool hasSearch, SourceRole expected)
    {
        Assert.Equal(expected, SourceRoles.For(kind, hasSearch));
    }

    /// <remarks>
    /// A kind nobody recognises has no role rather than a guessed one. A source
    /// that cannot be placed is one the health tool should flag, not one
    /// quietly asked the wrong question.
    /// </remarks>
    [Fact]
    public void AKindThisPluginDoesNotKnowHasNoRole()
    {
        Assert.Equal(SourceRole.None, SourceRoles.For("something-new", hasSearchAddress: true));
        Assert.Equal(SourceRole.None, SourceRoles.For(null, hasSearchAddress: false));
    }

    /// <remarks>
    /// A source searches from its own address when it has no separate one, which
    /// is how most of the catalogue works.
    /// </remarks>
    [Fact]
    public void ASourceWithoutASeparateSearchAddressSearchesFromItsOwn()
    {
        SourceDefinition source = new("1337x", "site", "https://www.1337x.to/search/{query}/1/");

        Assert.True(source.CanSearch);
        Assert.Equal("https://www.1337x.to/search/{query}/1/", source.SearchAddress);
    }

    /// <remarks>
    /// And a feed with no placeholder anywhere cannot be asked a question at
    /// all — putting one in the search set is what made forty identical
    /// requests a cycle.
    /// </remarks>
    [Fact]
    public void AFeedWithNoPlaceholderCannotBeSearched()
    {
        SourceDefinition source = new("SceneSource", "rss", "https://www.scnsrc.me/feed/");

        Assert.False(source.CanSearch);
        Assert.Null(source.SearchAddress);
    }

    /// <remarks>
    /// <para>
    /// <c>SearchGated</c> describes <c>SearchUrl</c>, and a source whose search
    /// <em>is</em> its own address has no <c>SearchUrl</c> for it to describe.
    /// Reading it anyway says "not gated" about an address the catalogue
    /// plainly marked gated.
    /// </para>
    /// <para>
    /// Measured, and it is why this exists: the health tool sent all four
    /// gated sites down plain HTTP on its first real run, and every one of them
    /// answered with a challenge that no amount of retrying was going to clear.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASourceWhoseSearchIsItsOwnAddressIsGatedByThatAddressesOwnFlag()
    {
        SourceDefinition oneAddress = new("1337x", "site", "https://www.1337x.to/search/{query}/1/")
        {
            Gated = true,
        };

        Assert.True(oneAddress.SearchAddressGated);

        // Two addresses, and only the second of them is behind a challenge.
        SourceDefinition twoAddresses = new("PreDB", "rss", "https://predb.me/?rss=1")
        {
            SearchUrl = "https://predb.me/?search={query}&rss=1",
            SearchGated = true,
        };

        Assert.True(twoAddresses.SearchAddressGated);
        Assert.False(twoAddresses.Gated);

        // And a source with one address that is not gated is not gated.
        Assert.False(new SourceDefinition("Nyaa", "torrent-rss", "https://nyaa.si/?page=rss&q={query}").SearchAddressGated);
    }

    /// <remarks>
    /// The placeholder is not legal in a URI, so an address carrying one still
    /// has to yield its host.
    /// </remarks>
    [Fact]
    public void AnAddressWithThePlaceholderStillYieldsItsHost()
    {
        Assert.Equal("www.1337x.to", SourceDefinition.HostOf("https://www.1337x.to/search/{query}/1/"));
        Assert.Null(SourceDefinition.HostOf("not an address"));
        Assert.Null(SourceDefinition.HostOf(null));
    }
}

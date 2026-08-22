using System.Text.Json;
using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Configuration;

public class CatalogueTests
{
    /// <remarks>
    /// <strong>C1.</strong> From the folder the assembly is really in — not
    /// <c>AppContext.BaseDirectory</c>, which for a plugin loaded into the
    /// server's process is the <em>server's</em> folder. 0.3.4 looked there,
    /// found nothing, fell back to three compiled-in sources, and ran for a day
    /// looking perfectly healthy while fourteen sources did not exist.
    /// </remarks>
    [Fact]
    public void TheCatalogueIsReadFromTheAssemblysOwnFolderAndHasMoreThanTenSources()
    {
        CapturingLogger log = new();

        IReadOnlyList<SourceDefinition> sources = new CatalogueLoader(log).Load();

        Assert.True(
            sources.Count > CatalogueLoader.Fewest,
            $"Only {sources.Count} sources loaded. Three once passed for a catalogue.");
        Assert.Empty(log.Lines);
    }

    /// <remarks>
    /// Every shipped source is placed. A kind nobody recognises has no role at
    /// all, so it would be in the catalogue and never asked anything — the
    /// quiet failure this whole slice is about.
    /// </remarks>
    [Fact]
    public void EveryShippedSourceHasARole()
    {
        IReadOnlyList<SourceDefinition> sources = new CatalogueLoader(new CapturingLogger()).Load();

        Assert.All(sources, source => Assert.NotEqual(SourceRole.None, source.Role));
    }

    /// <remarks>
    /// <strong>C4, from the other end.</strong> The registry test in Core asks
    /// that every reader the catalogue <em>names</em> exists. This asks the
    /// larger question: that every enabled source is read by a reader written
    /// for it. Ten of the seventeen name no reader at all and are placed by
    /// their kind, so a source whose kind nothing answers to would be fetched
    /// every cycle and read by whatever happened to be the fallback — which is
    /// C4 wearing a different hat.
    /// </remarks>
    [Fact]
    public void EveryEnabledShippedSourceIsReadByTheReaderNamedForIt()
    {
        Readers readers = Readers.Shipped();
        IReadOnlyList<SourceDefinition> sources = new CatalogueLoader(new CapturingLogger()).Load();

        Assert.NotEmpty(sources);

        foreach (SourceDefinition source in sources.Where(source => source.Enabled))
        {
            ISourceReader? reader = readers.For(source);

            Assert.True(reader is not null, $"Nothing reads {source.Name}.");
            Assert.Equal(source.Reader ?? source.Kind, reader!.Name);
        }
    }

    /// <remarks>
    /// A feed with no way to take a question is never in the search set. 0.3.4
    /// put SceneSource there and made forty identical requests per cycle, each
    /// one the same newest-first page.
    /// </remarks>
    [Fact]
    public void EverySourceThatCanBeAskedAQuestionHasSomewhereToAskIt()
    {
        IReadOnlyList<SourceDefinition> sources = new CatalogueLoader(new CapturingLogger()).Load();

        Assert.All(
            sources.Where(source => source.Role.HasFlag(SourceRole.Names) || source.Role.HasFlag(SourceRole.Indexer)),
            source => Assert.NotNull(source.SearchAddress));
    }

    /// <remarks>
    /// <para>
    /// A name source that can be asked about one show has to be asked about one
    /// show. SceneSource was in the catalogue as a feed and nothing else — the
    /// note said it had no search — so the plugin only ever read its newest
    /// posts, and a show that aired last week was left to srrDB's archive.
    /// </para>
    /// <para>
    /// That archive answers with years of foreign and 2160p releases, which the
    /// profile then refuses one after another with a good reason each time. The
    /// page reads as a plugin working hard and finding nothing, and the release
    /// the owner wanted was one request away the whole time:
    /// <c>/feed/?s=Silo</c> returns the season, in the same RSS the feed uses.
    /// </para>
    /// </remarks>
    [Fact]
    public void SceneSourceIsAskedAboutTheShowRatherThanOnlyReadForWhatIsNew()
    {
        SourceDefinition scenesource = Assert.Single(
            new CatalogueLoader(new CapturingLogger()).Load(),
            source => source.Name == "SceneSource");

        Assert.True(scenesource.CanSearch);
        Assert.Contains("{query}", scenesource.SearchAddress!, StringComparison.Ordinal);

        // Both addresses sit behind the same challenge, and a search treated as
        // plain HTTP is a 403 the plugin would report as the site refusing it.
        Assert.True(scenesource.SearchAddressGated);
    }

    /// <remarks>
    /// Missing is not the same as empty, and the difference has to be loud. The
    /// fallback is nothing at all rather than a smaller built-in set, because a
    /// partial fallback is exactly what made C1 invisible.
    /// </remarks>
    [Fact]
    public void AMissingCatalogueIsLoggedAndYieldsNothing()
    {
        CapturingLogger log = new();
        string empty = Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(empty);

        try
        {
            Assert.Empty(new CatalogueLoader(log).Load(empty));
            // At error level, not trace: a catalogue nobody hears about is
            // gone as surely as one nobody reported.
            Assert.Contains(
                log.Entries,
                entry => entry.Level == LogLevel.Error
                         && entry.Line.Contains("sources.json", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    /// <remarks>
    /// A catalogue that will not parse is the same failure wearing a different
    /// hat, and gets the same answer: nothing, said out loud.
    /// </remarks>
    [Fact]
    public void ACatalogueThatWillNotParseIsLoggedAndYieldsNothing()
    {
        CapturingLogger log = new();
        string folder = Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, CatalogueLoader.FileName), "{ not json");

        try
        {
            Assert.Empty(new CatalogueLoader(log).Load(folder));
            Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Error);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <remarks>
    /// Both directions. A host in the catalogue and not the manifest refuses
    /// instantly and reads exactly like a site with nothing to offer; a host in
    /// the manifest and not the catalogue is permission asked for nothing,
    /// which the owner has no way to judge.
    /// </remarks>
    [Fact]
    public void EveryHostInTheCatalogueIsInTheManifestAndNothingElseIs()
    {
        IReadOnlyList<SourceDefinition> shipped = new CatalogueLoader(new CapturingLogger()).Load();
        SourceCatalogue catalogue = SourceCatalogue.Build(shipped, [], []);

        string path = Path.Combine(AppContext.BaseDirectory, PluginIdentity.ManifestFileName);
        PluginManifest manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path))!;

        List<string> declared = manifest.Capabilities?.Network?.Hosts ?? [];

        // Including the hosts of a source that is switched off: the manifest
        // cannot change at runtime, so switching one on next month must not
        // need a new release to make it reachable.
        Assert.Equal(
            catalogue.EveryHost.Order(StringComparer.OrdinalIgnoreCase),
            declared.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <remarks>
    /// <strong>C2.</strong> Every host of every owner-configured source, search
    /// addresses included. 0.3.4 requested for the owner's indexers only and,
    /// on a default install where there are none, requested nothing at all
    /// while searching the whole shipped catalogue.
    /// </remarks>
    [Fact]
    public async Task EveryOwnerHostIsRequestedIncludingSearchAddresses()
    {
        FakeGrants grants = new();
        CapturingLogger log = new();

        IReadOnlyList<string> waiting = await new HostGrants(grants, log).RequestAsync(
            [
                new("Mine", "torznab", "https://feed.mine.example/all")
                {
                    SearchUrl = "https://search.mine.example/?q={query}",
                },
            ],
            CancellationToken.None);

        Assert.Equal(["feed.mine.example", "search.mine.example"], waiting);
        Assert.Equal(["feed.mine.example", "search.mine.example"], grants.Requested.Select(request => request.Host));
        Assert.All(grants.Requested, request => Assert.Equal(PluginGrantKind.NetworkHost, request.Kind));
        Assert.NotEmpty(log.Lines);
    }

    /// <remarks>
    /// It asks for the hosts it was handed and for nothing else. Shipped hosts
    /// are in the manifest — the test above holds those two lists together —
    /// and asking again at runtime would put a question to the owner that they
    /// answered by installing the plugin.
    /// </remarks>
    [Fact]
    public async Task NothingIsRequestedBeyondTheSourcesItWasGiven()
    {
        FakeGrants grants = new();
        IReadOnlyList<SourceDefinition> shipped = new CatalogueLoader(new CapturingLogger()).Load();

        await new HostGrants(grants, new CapturingLogger()).RequestAsync(
            [new("Mine", "torznab", "https://mine.example/?q={query}")],
            CancellationToken.None);

        Assert.Equal(["mine.example"], grants.Requested.Select(request => request.Host));
        Assert.DoesNotContain(
            grants.Requested.Select(request => request.Host),
            host => SourceCatalogue.Build(shipped, [], []).EveryHost.Contains(host, StringComparer.OrdinalIgnoreCase));
    }

    /// <remarks>
    /// A host already granted is not asked for again — on a plugin that has
    /// been running a while, that is every host, and a fresh prompt each cycle
    /// would train the owner to dismiss them.
    /// </remarks>
    [Fact]
    public async Task AHostAlreadyGrantedIsNotAskedForAgain()
    {
        FakeGrants grants = new();
        grants.Grant("mine.example");

        IReadOnlyList<string> waiting = await new HostGrants(grants, new CapturingLogger()).RequestAsync(
            [new("Mine", "torznab", "https://mine.example/?q={query}")],
            CancellationToken.None);

        Assert.Empty(waiting);
        Assert.Empty(grants.Requested);
    }
    /// <remarks>
    /// <para>
    /// docs/05-sources.md scopes Nyaa to <em>indexer (anime)</em>. Every other
    /// indexer names no library and is therefore for all of them, which is what
    /// keeps the field from switching anything off by omission.
    /// </para>
    /// <para>
    /// And it outranks them for the library it is scoped to. For anime it is
    /// often the only source that has the release at all, so when two sites
    /// serve the same copy to the same number of peers it is the one to take it
    /// from — which is what the priority column decides.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheAnimeIndexerIsScopedToAnimeAndOutranksTheGeneralOnes()
    {
        IReadOnlyList<SourceDefinition> shipped = new CatalogueLoader(new CapturingLogger()).Load();
        SourceDefinition nyaa = shipped.Single(one => one.Name == "Nyaa");

        Assert.Equal([LibraryKinds.Anime], nyaa.Libraries);

        SourceDefinition[] general =
        [
            .. shipped.Where(one => one.Role == SourceRole.Indexer && one.Libraries.Count == 0),
        ];

        Assert.NotEmpty(general);
        Assert.All(
            general,
            one => Assert.True(
                nyaa.Priority > one.Priority,
                $"{one.Name} at {one.Priority} outranks the anime indexer at {nyaa.Priority}."));
    }

}

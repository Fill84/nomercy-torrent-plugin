using System.Net;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// A source the owner added themselves, from the settings to the wire.
/// </summary>
public class OwnerSourceTests
{
    private const string Key = "an-owner-api-key-9f2c";

    /// <remarks>
    /// The key is substituted into the address at the moment of asking and
    /// comes from the secret store, never from the settings — which are
    /// whole-object JSON on disk.
    /// </remarks>
    [Fact]
    public async Task AnOwnerTorznabSourceIsBuiltAndAsked()
    {
        FakeHttp http = new FakeHttp().Answers(HttpStatusCode.OK, Feed);

        (ChallengeAwareFetch fetch, SourceDefinition source) = await OwnIndexer(http);

        Uri address = new(Query.Write(
            source.SearchAddress!.Replace("{apikey}", Key, StringComparison.Ordinal),
            "Silo S03E06",
            source.Query));

        FetchResult result = await fetch.GetAsync(address, gated: false, CancellationToken.None);

        Assert.True(result.Ok, result.Failure?.Reason);

        IReadOnlyList<SourceRow> rows = new TorznabReader().Read(result.Body!, address);

        SourceRow only = Assert.Single(rows);
        Assert.Equal("Silo.S03E06.1080p.WEB.H264-CAKES", only.Title);
        Assert.Equal("92D8A3F6864911EF292B4BE0DD5286406396D2B3", only.InfoHash);
        Assert.Equal(4024, only.Seeders);

        // It really was sent, or the test above proves nothing about secrecy.
        Assert.Contains(Key, http.Attempts[0].RequestUri?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    /// <remarks>
    /// And it appears in no line anybody will ever read. A refusal names the
    /// address it failed on — which is the whole of G1 — so the address has to
    /// be safe to say, and the failure, its message and every log line are all
    /// checked rather than only the one that was easiest to remember.
    /// </remarks>
    [Fact]
    public async Task TheOwnersApiKeyNeverAppearsInAFailureOrALogLine()
    {
        FakeHttp http = new FakeHttp().Answers(HttpStatusCode.TooManyRequests);

        (ChallengeAwareFetch fetch, SourceDefinition source) = await OwnIndexer(http);

        Uri address = new(Query.Write(
            source.SearchAddress!.Replace("{apikey}", Key, StringComparison.Ordinal),
            "Silo S03E06",
            source.Query));

        FetchResult result = await fetch.GetAsync(address, gated: false, CancellationToken.None);

        FetchFailure failure = Assert.IsType<FetchFailure>(result.Failure);

        Assert.DoesNotContain(Key, failure.Address, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, failure.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, failure.ToString(), StringComparison.Ordinal);

        // The name of the parameter survives, so somebody working out what went
        // wrong can still see which address it was.
        Assert.Contains("apikey=***", failure.Address, StringComparison.Ordinal);
        Assert.Contains("mine.example", failure.Address, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The settings themselves never hold it either: they are serialised whole
    /// into the host's configuration file in plaintext.
    /// </remarks>
    [Fact]
    public async Task TheKeyIsInTheSecretStoreAndNotInTheSettingsFile()
    {
        FakePluginContext context = new();
        SettingsStore store = new(context.Config, context.Secrets);

        Settings settings = new()
        {
            IncompleteFolder = Path.GetTempPath(),
            IntakeFolder = Path.GetTempPath(),
        };
        settings.Indexers.Add(new()
        {
            Id = "own-1",
            Name = "Mine",
            Kind = "torznab",
            Address = "https://mine.example/api?t=search&q={query}&apikey={apikey}",
        });

        await store.SaveAsync(settings, CancellationToken.None);
        await store.SetSecretAsync(SettingsStore.IndexerApiKey("own-1"), Key, CancellationToken.None);

        Assert.DoesNotContain(Key, context.Config.Written, StringComparison.Ordinal);
        Assert.Equal(Key, await context.Secrets.GetAsync(SettingsStore.IndexerApiKey("own-1")));
    }

    private static async Task<(ChallengeAwareFetch Fetch, SourceDefinition Source)> OwnIndexer(FakeHttp http)
    {
        FakePluginContext context = new();
        SettingsStore store = new(context.Config, context.Secrets);
        await store.SetSecretAsync(SettingsStore.IndexerApiKey("own-1"), Key, CancellationToken.None);

        FakeGrants grants = new();
        grants.Grant("mine.example");

        HostGate gate = new(TimeProvider.System);
        gate.Configure("mine.example", TimeSpan.Zero);

        SourceDefinition source = new(
            "Mine",
            "torznab",
            "https://mine.example/api?t=search&q={query}&apikey={apikey}");

        return (new(http.Client(), gate, grants, new ClearanceStore()), source);
    }

    /// <summary>
    /// A Torznab answer, in the shape an indexer manager sends.
    /// </summary>
    /// <remarks>
    /// Written here rather than captured: this plugin has no Torznab server to
    /// ask, and one cannot be conjured. It is the published shape, and the
    /// reader is the only one in this repository without a real capture behind
    /// it — which is recorded in `SPRINTS.md` rather than left to be assumed.
    /// </remarks>
    private const string Feed =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
          <channel>
            <item>
              <title>Silo.S03E06.1080p.WEB.H264-CAKES</title>
              <link>https://mine.example/download/1</link>
              <enclosure url="magnet:?xt=urn:btih:92D8A3F6864911EF292B4BE0DD5286406396D2B3" type="application/x-bittorrent" />
              <torznab:attr name="seeders" value="4024" />
              <torznab:attr name="peers" value="1793" />
              <torznab:attr name="infohash" value="92d8a3f6864911ef292b4be0dd5286406396d2b3" />
              <torznab:attr name="size" value="4388742440" />
            </item>
          </channel>
        </rss>
        """;
}

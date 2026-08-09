// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text.Json;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Configuration;

public class SettingsGatewayTests
{
    [Fact]
    public async Task LoadAsync_ReturnsDefaultsWhenNothingIsSaved()
    {
        FakeConfiguration configuration = new();
        FakeSecretStore secretStore = new();
        SettingsGateway gateway = new(configuration, secretStore);

        LoadedSettings loaded = await gateway.LoadAsync(CancellationToken.None);

        loaded.Settings.TransfersCron.Should().Be("* * * * *");
        loaded.Settings.FeedCron.Should().Be("*/15 * * * *");
        loaded.Settings.SearchCron.Should().Be("0 */6 * * *");
        loaded.Settings.MaintenanceCron.Should().Be("0 4 * * *");
    }

    [Fact]
    public async Task LoadAsync_ReturnsSavedSettings()
    {
        FakeConfiguration configuration = new();
        TorrentDownloaderSettings stored = new()
        {
            TransfersCron = "*/5 * * * *",
            IncompleteFolder = "/downloads/incomplete",
            IntakeFolder = "/downloads/intake",
        };
        configuration.SaveConfiguration(stored);
        FakeSecretStore secretStore = new();
        SettingsGateway gateway = new(configuration, secretStore);

        LoadedSettings loaded = await gateway.LoadAsync(CancellationToken.None);

        loaded.Settings.TransfersCron.Should().Be("*/5 * * * *");
        loaded.Settings.IncompleteFolder.Should().Be("/downloads/incomplete");
        loaded.Settings.IntakeFolder.Should().Be("/downloads/intake");
    }

    [Fact]
    public async Task SaveAsync_WritesSettingsToConfiguration()
    {
        FakeConfiguration configuration = new();
        FakeSecretStore secretStore = new();
        SettingsGateway gateway = new(configuration, secretStore);
        TorrentDownloaderSettings settings = new() { IncompleteFolder = "/downloads/incomplete" };

        await gateway.SaveAsync(new LoadedSettings(settings, [], []), CancellationToken.None);

        configuration.Stored.Should().BeOfType<TorrentDownloaderSettings>();
        ((TorrentDownloaderSettings)configuration.Stored!).IncompleteFolder.Should().Be("/downloads/incomplete");
    }

    [Fact]
    public async Task SaveAsync_SendsAnIndexerApiKeyToTheSecretStore()
    {
        FakeConfiguration configuration = new();
        FakeSecretStore secretStore = new();
        SettingsGateway gateway = new(configuration, secretStore);
        TorrentDownloaderSettings settings = new() { Indexers = [new IndexerSettings { Name = "Prowlarr" }] };

        await gateway.SaveAsync(
            new LoadedSettings(settings, [new IndexerSecret("Prowlarr", "super-secret-api-key")], []),
            CancellationToken.None
        );

        string? stored = await secretStore.GetAsync("indexer:Prowlarr:apikey", CancellationToken.None);
        stored.Should().Be("super-secret-api-key");
    }

    [Fact]
    public async Task SaveAsync_NeverWritesAnApiKeyIntoConfiguration()
    {
        FakeConfiguration configuration = new();
        FakeSecretStore secretStore = new();
        SettingsGateway gateway = new(configuration, secretStore);
        TorrentDownloaderSettings settings = new() { Indexers = [new IndexerSettings { Name = "Prowlarr" }] };
        const string apiKey = "super-secret-api-key-that-must-never-leak";

        await gateway.SaveAsync(
            new LoadedSettings(settings, [new IndexerSecret("Prowlarr", apiKey)], []),
            CancellationToken.None
        );

        string serialized = string.Join(
            '\n',
            configuration.SavedObjects.Select(saved => JsonSerializer.Serialize(saved, saved.GetType()))
        );
        serialized.Should().NotContain(apiKey);
    }

    [Fact]
    public async Task SaveAsync_NeverWritesAClientPasswordIntoConfiguration()
    {
        FakeConfiguration configuration = new();
        FakeSecretStore secretStore = new();
        SettingsGateway gateway = new(configuration, secretStore);
        TorrentDownloaderSettings settings = new() { Clients = [new TorrentClientSettings { Name = "qBittorrent" }] };
        const string password = "super-secret-client-password-that-must-never-leak";

        await gateway.SaveAsync(
            new LoadedSettings(settings, [], [new ClientSecret("qBittorrent", password)]),
            CancellationToken.None
        );

        string serialized = string.Join(
            '\n',
            configuration.SavedObjects.Select(saved => JsonSerializer.Serialize(saved, saved.GetType()))
        );
        serialized.Should().NotContain(password);
    }

    [Fact]
    public async Task LoadAsync_FillsAnApiKeyBackFromTheSecretStore()
    {
        FakeConfiguration configuration = new();
        FakeSecretStore secretStore = new();
        SettingsGateway gateway = new(configuration, secretStore);
        TorrentDownloaderSettings settings = new() { Indexers = [new IndexerSettings { Name = "Prowlarr" }] };
        await gateway.SaveAsync(
            new LoadedSettings(settings, [new IndexerSecret("Prowlarr", "the-api-key")], []),
            CancellationToken.None
        );

        LoadedSettings loaded = await gateway.LoadAsync(CancellationToken.None);

        loaded.IndexerSecrets.Should().ContainSingle(secret => secret.Name == "Prowlarr" && secret.ApiKey == "the-api-key");
    }

    [Fact]
    public async Task SaveAsync_RemovesASecretWhenItsEntryIsDeleted()
    {
        FakeConfiguration configuration = new();
        FakeSecretStore secretStore = new();
        SettingsGateway gateway = new(configuration, secretStore);
        TorrentDownloaderSettings withIndexer = new() { Indexers = [new IndexerSettings { Name = "Prowlarr" }] };
        await gateway.SaveAsync(
            new LoadedSettings(withIndexer, [new IndexerSecret("Prowlarr", "the-api-key")], []),
            CancellationToken.None
        );

        TorrentDownloaderSettings withoutIndexer = new() { Indexers = [] };
        await gateway.SaveAsync(new LoadedSettings(withoutIndexer, [], []), CancellationToken.None);

        string? stored = await secretStore.GetAsync("indexer:Prowlarr:apikey", CancellationToken.None);
        stored.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_LeavesAnExistingSecretAloneWhenTheFormSubmitsAnEmptyValue()
    {
        FakeConfiguration configuration = new();
        FakeSecretStore secretStore = new();
        SettingsGateway gateway = new(configuration, secretStore);
        TorrentDownloaderSettings settings = new() { Indexers = [new IndexerSettings { Name = "Prowlarr" }] };
        await gateway.SaveAsync(
            new LoadedSettings(settings, [new IndexerSecret("Prowlarr", "the-original-api-key")], []),
            CancellationToken.None
        );

        await gateway.SaveAsync(
            new LoadedSettings(settings, [new IndexerSecret("Prowlarr", string.Empty)], []),
            CancellationToken.None
        );

        string? stored = await secretStore.GetAsync("indexer:Prowlarr:apikey", CancellationToken.None);
        stored.Should().Be("the-original-api-key");
    }

    [Fact]
    public async Task SecretKeyFor_IsStableAcrossRenamesOfUnrelatedEntries()
    {
        FakeConfiguration configuration = new();
        FakeSecretStore secretStore = new();
        SettingsGateway gateway = new(configuration, secretStore);
        TorrentDownloaderSettings original = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr" }, new IndexerSettings { Name = "Jackett" }],
        };
        await gateway.SaveAsync(
            new LoadedSettings(
                original,
                [new IndexerSecret("Prowlarr", "prowlarr-key"), new IndexerSecret("Jackett", "jackett-key")],
                []
            ),
            CancellationToken.None
        );

        TorrentDownloaderSettings renamed = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr2" }, new IndexerSettings { Name = "Jackett" }],
        };
        await gateway.SaveAsync(
            new LoadedSettings(
                renamed,
                [new IndexerSecret("Prowlarr2", "prowlarr-key"), new IndexerSecret("Jackett", string.Empty)],
                []
            ),
            CancellationToken.None
        );

        string? jackettSecret = await secretStore.GetAsync("indexer:Jackett:apikey", CancellationToken.None);
        jackettSecret.Should().Be("jackett-key");
    }

    // --- private trackers ---------------------------------------------------------

    // The announce URL is not a field with a secret in it, it is the secret: the passkey
    // in it is the account. Configuration is whole-object JSON on disk, so this asserts
    // on the serialised settings rather than on the object - a leak is what lands there.
    [Fact]
    public async Task SaveAsync_KeepsAPrivateTrackersAnnounceUrlOutOfConfigurationEntirely()
    {
        FakeConfiguration configuration = new();
        FakeSecretStore secretStore = new();
        SettingsGateway gateway = new(configuration, secretStore);
        TorrentDownloaderSettings settings = new()
        {
            PrivateTrackers = [new PrivateTrackerSettings { Name = "RedFish", Seed = true }],
        };

        await gateway.SaveAsync(
            new LoadedSettings(
                settings,
                [],
                [],
                [new PrivateTrackerSecret("RedFish", "https://redfish.test/announce/PASSKEY123", null)]
            ),
            CancellationToken.None
        );

        JsonSerializer.Serialize(configuration.Stored).Should().NotContain("PASSKEY123").And.NotContain("redfish.test");
        (await secretStore.GetAsync("tracker:RedFish:announce", CancellationToken.None))
            .Should().Be("https://redfish.test/announce/PASSKEY123");
    }

    [Fact]
    public async Task LoadAsync_BringsBackAPrivateTrackersAnnounceUrlAndApiKey()
    {
        FakeConfiguration configuration = new();
        FakeSecretStore secretStore = new();
        SettingsGateway gateway = new(configuration, secretStore);
        configuration.Stored = new TorrentDownloaderSettings
        {
            PrivateTrackers = [new PrivateTrackerSettings { Name = "RedFish" }],
        };
        await secretStore.SetAsync("tracker:RedFish:announce", "https://redfish.test/announce/PASSKEY123", CancellationToken.None);
        await secretStore.SetAsync("tracker:RedFish:apikey", "torznab-key", CancellationToken.None);

        LoadedSettings loaded = await gateway.LoadAsync(CancellationToken.None);

        PrivateTrackerSecret secret = loaded.PrivateTrackerSecrets.Should().ContainSingle().Which;
        secret.AnnounceUrl.Should().Be("https://redfish.test/announce/PASSKEY123");
        secret.ApiKey.Should().Be("torznab-key");
    }

    // An entry whose announce URL never made it to the store cannot match a torrent to
    // anything - the host it announces to is its whole identity. Handing it on anyway
    // would put a nameless tracker in the registry and invite a crash further down.
    [Fact]
    public async Task LoadAsync_SkipsAPrivateTrackerWithNoAnnounceUrlStored()
    {
        FakeConfiguration configuration = new();
        FakeSecretStore secretStore = new();
        SettingsGateway gateway = new(configuration, secretStore);
        configuration.Stored = new TorrentDownloaderSettings
        {
            PrivateTrackers = [new PrivateTrackerSettings { Name = "HalfAdded" }],
        };

        LoadedSettings loaded = await gateway.LoadAsync(CancellationToken.None);

        loaded.PrivateTrackerSecrets.Should().BeEmpty();
    }

    // The orphan sweep deletes secrets no live entry owns, and a tracker owns two. The
    // naive version keeps only the announce key alive and silently drops the API key on
    // the next unrelated save.
    [Fact]
    public async Task SaveAsync_KeepsBothOfAPrivateTrackersSecretsWhenSomethingElseIsSaved()
    {
        FakeConfiguration configuration = new();
        FakeSecretStore secretStore = new();
        SettingsGateway gateway = new(configuration, secretStore);
        TorrentDownloaderSettings settings = new()
        {
            PrivateTrackers = [new PrivateTrackerSettings { Name = "RedFish" }],
        };

        await gateway.SaveAsync(
            new LoadedSettings(settings, [], [], [new PrivateTrackerSecret("RedFish", "https://redfish.test/a/KEY", "torznab-key")]),
            CancellationToken.None
        );

        // A later save that carries no tracker secrets at all - the general form, say.
        await gateway.SaveAsync(new LoadedSettings(settings, [], []), CancellationToken.None);

        (await secretStore.GetAsync("tracker:RedFish:announce", CancellationToken.None)).Should().Be("https://redfish.test/a/KEY");
        (await secretStore.GetAsync("tracker:RedFish:apikey", CancellationToken.None)).Should().Be("torznab-key");
    }

    [Fact]
    public async Task SaveAsync_ForgetsBothSecretsOfAPrivateTrackerThatWasRemoved()
    {
        FakeConfiguration configuration = new();
        FakeSecretStore secretStore = new();
        SettingsGateway gateway = new(configuration, secretStore);
        TorrentDownloaderSettings withTracker = new()
        {
            PrivateTrackers = [new PrivateTrackerSettings { Name = "RedFish" }],
        };

        await gateway.SaveAsync(
            new LoadedSettings(withTracker, [], [], [new PrivateTrackerSecret("RedFish", "https://redfish.test/a/KEY", "torznab-key")]),
            CancellationToken.None
        );
        await gateway.SaveAsync(new LoadedSettings(new TorrentDownloaderSettings(), [], []), CancellationToken.None);

        // A passkey outliving the entry that owned it is a credential nobody is looking
        // after any more.
        (await secretStore.GetAsync("tracker:RedFish:announce", CancellationToken.None)).Should().BeNull();
        (await secretStore.GetAsync("tracker:RedFish:apikey", CancellationToken.None)).Should().BeNull();
    }
}

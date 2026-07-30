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
}

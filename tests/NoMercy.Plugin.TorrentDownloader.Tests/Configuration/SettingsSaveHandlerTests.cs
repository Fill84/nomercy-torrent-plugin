// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text.Json;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Configuration;

public class SettingsSaveHandlerTests
{
    private static async Task<(FakeConfiguration Configuration, FakeSecretStore Secrets, SettingsSaveHandler Handler)> SeededAsync(
        TorrentDownloaderSettings initialSettings,
        params (string Key, string Value)[] secrets
    )
    {
        FakeConfiguration configuration = new();
        FakeSecretStore secretStore = new();
        SettingsGateway gateway = new(configuration, secretStore);
        await gateway.SaveAsync(new LoadedSettings(initialSettings, [], []), CancellationToken.None);

        foreach ((string key, string value) in secrets)
        {
            await secretStore.SetAsync(key, value, CancellationToken.None);
        }

        return (configuration, secretStore, new SettingsSaveHandler(gateway));
    }

    // THE data-loss test. A naive implementation - reconstruct settings from only the
    // submitted fields and call SaveAsync directly - passes every other test in this file
    // and fails only this one, because the general form carries neither an indexer nor a
    // client: reconstructing "the world" from it alone produces empty Indexers/Clients
    // lists, and SaveAsync's orphan sweep then deletes every secret whose entry vanished.
    [Fact]
    public async Task HandleAsync_GeneralForm_LeavesExistingIndexersClientsAndSecretsIntact()
    {
        TorrentDownloaderSettings initial = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" }],
            Clients = [new TorrentClientSettings { Name = "qBit", Url = "https://qbit.local" }],
        };
        (FakeConfiguration configuration, FakeSecretStore secrets, SettingsSaveHandler handler) =
            await SeededAsync(initial, ("indexer:Prowlarr:apikey", "prowlarr-key"), ("client:qBit:password", "qbit-password"));

        SaveSettingsRequest request = new()
        {
            TransfersCron = "*/2 * * * *",
            FeedCron = "*/20 * * * *",
            SearchCron = "0 */3 * * *",
            MaintenanceCron = "0 5 * * *",
            IncompleteFolder = "/downloads/incomplete",
            IntakeFolder = "/downloads/intake",
        };

        SaveSettingsOutcome outcome = await handler.HandleAsync(request, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Indexers.Should().ContainSingle(indexer => indexer.Name == "Prowlarr");
        saved.Clients.Should().ContainSingle(client => client.Name == "qBit");
        (await secrets.GetAsync("indexer:Prowlarr:apikey", CancellationToken.None)).Should().Be("prowlarr-key");
        (await secrets.GetAsync("client:qBit:password", CancellationToken.None)).Should().Be("qbit-password");
    }

    // The general form's data-loss case was covered; these two were not, and both mutants
    // survived the whole suite: making the indexer branch drop every client, and the client
    // branch drop every indexer, changed no test's result. Same hazard as the general form -
    // a partial submission overwriting a section it never addressed - one section across.
    [Fact]
    public async Task HandleAsync_IndexerForm_LeavesEveryDownloadClientAndItsSecretIntact()
    {
        TorrentDownloaderSettings initial = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" }],
            Clients =
            [
                new TorrentClientSettings { Name = "qBit", Url = "https://qbit.local" },
                new TorrentClientSettings { Name = "Deluge", Url = "https://deluge.local" },
            ],
        };
        (FakeConfiguration configuration, FakeSecretStore secrets, SettingsSaveHandler handler) =
            await SeededAsync(initial, ("client:qBit:password", "qbit-password"));

        SaveSettingsRequest request = new()
        {
            IndexerName = "Prowlarr",
            Name = "Prowlarr",
            Url = "https://prowlarr.local:9696",
        };

        SaveSettingsOutcome outcome = await handler.HandleAsync(request, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Clients.Should().HaveCount(2);
        saved.Clients.Should().Contain(client => client.Name == "qBit");
        saved.Clients.Should().Contain(client => client.Name == "Deluge");
        (await secrets.GetAsync("client:qBit:password", CancellationToken.None)).Should().Be("qbit-password");
    }

    [Fact]
    public async Task HandleAsync_ClientForm_LeavesEveryIndexerAndItsSecretIntact()
    {
        TorrentDownloaderSettings initial = new()
        {
            Indexers =
            [
                new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" },
                new IndexerSettings { Name = "Jackett", Url = "https://jackett.local" },
            ],
            Clients = [new TorrentClientSettings { Name = "qBit", Url = "https://qbit.local" }],
        };
        (FakeConfiguration configuration, FakeSecretStore secrets, SettingsSaveHandler handler) =
            await SeededAsync(initial, ("indexer:Prowlarr:apikey", "prowlarr-key"));

        SaveSettingsRequest request = new()
        {
            ClientName = "qBit",
            Name = "qBit",
            Url = "https://qbit.local:8080",
        };

        SaveSettingsOutcome outcome = await handler.HandleAsync(request, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Indexers.Should().HaveCount(2);
        saved.Indexers.Should().Contain(indexer => indexer.Name == "Prowlarr");
        saved.Indexers.Should().Contain(indexer => indexer.Name == "Jackett");
        (await secrets.GetAsync("indexer:Prowlarr:apikey", CancellationToken.None)).Should().Be("prowlarr-key");
    }

    [Fact]
    public async Task HandleAsync_GeneralForm_UpdatesCronAndFolders()
    {
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(new TorrentDownloaderSettings());

        SaveSettingsRequest request = new()
        {
            TransfersCron = "*/2 * * * *",
            FeedCron = "*/20 * * * *",
            SearchCron = "0 */3 * * *",
            MaintenanceCron = "0 5 * * *",
            IncompleteFolder = "/downloads/incomplete",
            IntakeFolder = "/downloads/intake",
        };

        await handler.HandleAsync(request, CancellationToken.None);

        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.TransfersCron.Should().Be("*/2 * * * *");
        saved.IncompleteFolder.Should().Be("/downloads/incomplete");
        saved.IntakeFolder.Should().Be("/downloads/intake");
    }

    [Theory]
    [InlineData(null, "*/1 * * * *", "*/1 * * * *", "*/1 * * * *")]
    [InlineData("", "*/1 * * * *", "*/1 * * * *", "*/1 * * * *")]
    [InlineData("   ", "*/1 * * * *", "*/1 * * * *", "*/1 * * * *")]
    public async Task HandleAsync_GeneralForm_RejectsABlankCronWithoutPersisting(
        string? transfersCron,
        string feedCron,
        string searchCron,
        string maintenanceCron
    )
    {
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(new TorrentDownloaderSettings());
        int savesBefore = configuration.SavedObjects.Count;

        SaveSettingsRequest request = new()
        {
            TransfersCron = transfersCron,
            FeedCron = feedCron,
            SearchCron = searchCron,
            MaintenanceCron = maintenanceCron,
        };

        SaveSettingsOutcome outcome = await handler.HandleAsync(request, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().NotBeNullOrWhiteSpace();
        configuration.SavedObjects.Should().HaveCount(savesBefore);
    }

    [Fact]
    public async Task HandleAsync_IndexerForm_DoesNotDisturbAnotherIndexersFieldsOrSecret()
    {
        TorrentDownloaderSettings initial = new()
        {
            Indexers =
            [
                new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local", Priority = 25 },
                new IndexerSettings { Name = "Jackett", Url = "https://jackett.local", Priority = 10 },
            ],
        };
        (FakeConfiguration configuration, FakeSecretStore secrets, SettingsSaveHandler handler) =
            await SeededAsync(initial, ("indexer:Jackett:apikey", "jackett-key"));

        SaveSettingsRequest request = new()
        {
            IndexerName = "Prowlarr",
            Name = "Prowlarr",
            Url = "https://prowlarr.example",
            Priority = 5,
        };

        SaveSettingsOutcome outcome = await handler.HandleAsync(request, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Indexers.Should().ContainSingle(indexer => indexer.Name == "Prowlarr" && indexer.Url == "https://prowlarr.example" && indexer.Priority == 5);
        IndexerSettings jackett = saved.Indexers.Should().ContainSingle(indexer => indexer.Name == "Jackett").Which;
        jackett.Url.Should().Be("https://jackett.local");
        jackett.Priority.Should().Be(10);
        (await secrets.GetAsync("indexer:Jackett:apikey", CancellationToken.None)).Should().Be("jackett-key");
    }

    [Fact]
    public async Task HandleAsync_IndexerForm_EmptySubmittedSecretLeavesStoredSecretInPlace()
    {
        TorrentDownloaderSettings initial = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" }],
        };
        (FakeConfiguration configuration, FakeSecretStore secrets, SettingsSaveHandler handler) =
            await SeededAsync(initial, ("indexer:Prowlarr:apikey", "the-original-key"));

        SaveSettingsRequest request = new()
        {
            IndexerName = "Prowlarr",
            Name = "Prowlarr",
            Url = "https://prowlarr.local",
            ApiKey = string.Empty,
        };

        await handler.HandleAsync(request, CancellationToken.None);

        (await secrets.GetAsync("indexer:Prowlarr:apikey", CancellationToken.None)).Should().Be("the-original-key");
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Indexers.Should().ContainSingle(indexer => indexer.Name == "Prowlarr");
    }

    [Fact]
    public async Task HandleAsync_IndexerForm_SubmittedSecretIsWrittenToTheSecretStoreAndNeverToConfiguration()
    {
        TorrentDownloaderSettings initial = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" }],
        };
        (FakeConfiguration configuration, FakeSecretStore secrets, SettingsSaveHandler handler) = await SeededAsync(initial);
        const string apiKey = "brand-new-api-key-that-must-never-leak";

        SaveSettingsRequest request = new()
        {
            IndexerName = "Prowlarr",
            Name = "Prowlarr",
            Url = "https://prowlarr.local",
            ApiKey = apiKey,
        };

        await handler.HandleAsync(request, CancellationToken.None);

        (await secrets.GetAsync("indexer:Prowlarr:apikey", CancellationToken.None)).Should().Be(apiKey);
        string serialized = string.Join(
            '\n',
            configuration.SavedObjects.Select(saved => JsonSerializer.Serialize(saved, saved.GetType()))
        );
        serialized.Should().NotContain(apiKey);
    }

    [Fact]
    public async Task HandleAsync_IndexerForm_RejectsANonAbsoluteUrlWithoutPersisting()
    {
        TorrentDownloaderSettings initial = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" }],
        };
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(initial);
        int savesBefore = configuration.SavedObjects.Count;

        SaveSettingsRequest request = new() { IndexerName = "Prowlarr", Name = "Prowlarr", Url = "not-a-url" };

        SaveSettingsOutcome outcome = await handler.HandleAsync(request, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().NotBeNullOrWhiteSpace();
        configuration.SavedObjects.Should().HaveCount(savesBefore);
    }

    [Fact]
    public async Task HandleAsync_IndexerForm_UnknownIndexerNameRejectsWithoutPersisting()
    {
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(new TorrentDownloaderSettings());
        int savesBefore = configuration.SavedObjects.Count;

        SaveSettingsRequest request = new() { IndexerName = "DoesNotExist", Name = "DoesNotExist", Url = "https://example.local" };

        SaveSettingsOutcome outcome = await handler.HandleAsync(request, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        configuration.SavedObjects.Should().HaveCount(savesBefore);
    }

    // The chosen behaviour for a rename: honour it, and carry the secret forward to the new
    // name rather than orphaning it. SaveAsync's own key derivation would otherwise silently
    // drop this credential the moment its entry's name changed.
    [Fact]
    public async Task HandleAsync_RenamingAnIndexer_PreservesItsSecretUnderTheNewName()
    {
        TorrentDownloaderSettings initial = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" }],
        };
        (FakeConfiguration configuration, FakeSecretStore secrets, SettingsSaveHandler handler) =
            await SeededAsync(initial, ("indexer:Prowlarr:apikey", "the-original-key"));

        SaveSettingsRequest request = new()
        {
            IndexerName = "Prowlarr",
            Name = "Prowlarr2",
            Url = "https://prowlarr.local",
        };

        SaveSettingsOutcome outcome = await handler.HandleAsync(request, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Indexers.Should().ContainSingle(indexer => indexer.Name == "Prowlarr2");
        (await secrets.GetAsync("indexer:Prowlarr2:apikey", CancellationToken.None)).Should().Be("the-original-key");
        (await secrets.GetAsync("indexer:Prowlarr:apikey", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_ClientForm_DoesNotDisturbAnotherClientsFieldsOrSecret()
    {
        TorrentDownloaderSettings initial = new()
        {
            Clients =
            [
                new TorrentClientSettings { Name = "qBit", Url = "https://qbit.local", Username = "admin" },
                new TorrentClientSettings { Name = "Transmission", Url = "https://transmission.local", Username = "root" },
            ],
        };
        (FakeConfiguration configuration, FakeSecretStore secrets, SettingsSaveHandler handler) =
            await SeededAsync(initial, ("client:Transmission:password", "transmission-password"));

        SaveSettingsRequest request = new()
        {
            ClientName = "qBit",
            Name = "qBit",
            Url = "https://qbit.example",
            Username = "changed",
        };

        SaveSettingsOutcome outcome = await handler.HandleAsync(request, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Clients.Should().ContainSingle(client => client.Name == "qBit" && client.Url == "https://qbit.example" && client.Username == "changed");
        TorrentClientSettings transmission = saved.Clients.Should().ContainSingle(client => client.Name == "Transmission").Which;
        transmission.Url.Should().Be("https://transmission.local");
        transmission.Username.Should().Be("root");
        (await secrets.GetAsync("client:Transmission:password", CancellationToken.None)).Should().Be("transmission-password");
    }

    [Fact]
    public async Task HandleAsync_ClientForm_EmptySubmittedPasswordLeavesStoredPasswordInPlace()
    {
        TorrentDownloaderSettings initial = new()
        {
            Clients = [new TorrentClientSettings { Name = "qBit", Url = "https://qbit.local" }],
        };
        (FakeConfiguration configuration, FakeSecretStore secrets, SettingsSaveHandler handler) =
            await SeededAsync(initial, ("client:qBit:password", "the-original-password"));

        SaveSettingsRequest request = new()
        {
            ClientName = "qBit",
            Name = "qBit",
            Url = "https://qbit.local",
            Password = string.Empty,
        };

        await handler.HandleAsync(request, CancellationToken.None);

        (await secrets.GetAsync("client:qBit:password", CancellationToken.None)).Should().Be("the-original-password");
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Clients.Should().ContainSingle(client => client.Name == "qBit");
    }

    [Fact]
    public async Task HandleAsync_ClientForm_RejectsANonAbsoluteUrlWithoutPersisting()
    {
        TorrentDownloaderSettings initial = new()
        {
            Clients = [new TorrentClientSettings { Name = "qBit", Url = "https://qbit.local" }],
        };
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(initial);
        int savesBefore = configuration.SavedObjects.Count;

        SaveSettingsRequest request = new() { ClientName = "qBit", Name = "qBit", Url = "not-a-url" };

        SaveSettingsOutcome outcome = await handler.HandleAsync(request, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        configuration.SavedObjects.Should().HaveCount(savesBefore);
    }
}

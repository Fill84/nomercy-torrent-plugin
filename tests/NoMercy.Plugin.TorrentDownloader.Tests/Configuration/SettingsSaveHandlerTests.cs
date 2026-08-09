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

        // Fixed rather than real time, so every test built on this helper stays
        // deterministic; the handful of tests below that actually assert on
        // LastSavedAtUtc build their own FakeClock so they can hold a reference to it.
        FakeClock clock = new(new DateTimeOffset(2026, 7, 31, 1, 59, 0, TimeSpan.Zero));
        return (configuration, secretStore, new SettingsSaveHandler(gateway, clock));
    }

    // THE data-loss test. A naive implementation - reconstruct settings from only the
    // submitted fields and call SaveAsync directly - passes every other test in this file
    // and fails only this one, because the general form carries neither an indexer nor a
    // client: reconstructing "the world" from it alone produces empty Indexers/Clients
    // lists, and SaveAsync's orphan sweep then deletes every secret whose entry vanished.
    [Fact]
    public async Task HandleGeneralAsync_LeavesExistingIndexersClientsAndSecretsIntact()
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

        SaveSettingsOutcome outcome = await handler.HandleGeneralAsync(request, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Indexers.Should().ContainSingle(indexer => indexer.Name == "Prowlarr");
        saved.Clients.Should().ContainSingle(client => client.Name == "qBit");
        (await secrets.GetAsync("indexer:Prowlarr:apikey", CancellationToken.None)).Should().Be("prowlarr-key");
        (await secrets.GetAsync("client:qBit:password", CancellationToken.None)).Should().Be("qbit-password");
    }

    // The old shape could not express this at all: an indexer form only ever posts its own
    // fields (name, kind, url, priority, enabled, minimumIntervalSeconds, categories,
    // apiKey), never a cron field and never an out-of-band "indexerName" - the client's
    // PluginForm submit discards whatever payload an action intent was built with, so a
    // field placed there never reaches the server. Routing by index (see
    // TorrentDownloaderSettingsController.SaveIndexer) rather than by an identity field on
    // the body is what makes this body alone enough to update the right indexer.
    [Fact]
    public async Task HandleIndexerAsync_BodyWithOnlyTheIndexerFormsFields_StillUpdatesThatIndexer()
    {
        TorrentDownloaderSettings initial = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local", Priority = 25 }],
        };
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(initial);

        SaveSettingsRequest request = new()
        {
            Name = "Prowlarr",
            Kind = "torznab",
            Url = "https://prowlarr.local:9696",
            Priority = 5,
            Enabled = true,
            MinimumIntervalSeconds = 60,
            Categories = "5000,5070",
        };

        SaveSettingsOutcome outcome = await handler.HandleIndexerAsync(0, request, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Indexers.Should().ContainSingle(
            indexer => indexer.Name == "Prowlarr" && indexer.Url == "https://prowlarr.local:9696" && indexer.Priority == 5
        );
    }

    // Same defect, one section across: a client form's body never carries "clientName"
    // either, so routing by index has to be enough on its own here too.
    [Fact]
    public async Task HandleClientAsync_BodyWithOnlyTheClientFormsFields_StillUpdatesThatClient()
    {
        TorrentDownloaderSettings initial = new()
        {
            Clients = [new TorrentClientSettings { Name = "qBit", Url = "https://qbit.local" }],
        };
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(initial);

        SaveSettingsRequest request = new()
        {
            Name = "qBit",
            Kind = "qbittorrent",
            Url = "https://qbit.local:8080",
            Username = "changed",
            Enabled = true,
        };

        SaveSettingsOutcome outcome = await handler.HandleClientAsync(0, request, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Clients.Should().ContainSingle(
            client => client.Name == "qBit" && client.Url == "https://qbit.local:8080" && client.Username == "changed"
        );
    }

    [Fact]
    public async Task HandleIndexerAsync_OutOfRangeIndexFailsCleanlyWithoutPersisting()
    {
        TorrentDownloaderSettings initial = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" }],
        };
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(initial);
        int savesBefore = configuration.SavedObjects.Count;

        SaveSettingsRequest request = new() { Name = "Prowlarr", Url = "https://prowlarr.example" };

        SaveSettingsOutcome outcome = await handler.HandleIndexerAsync(4, request, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("4");
        configuration.SavedObjects.Should().HaveCount(savesBefore);
    }

    [Fact]
    public async Task HandleClientAsync_OutOfRangeIndexFailsCleanlyWithoutPersisting()
    {
        TorrentDownloaderSettings initial = new()
        {
            Clients = [new TorrentClientSettings { Name = "qBit", Url = "https://qbit.local" }],
        };
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(initial);
        int savesBefore = configuration.SavedObjects.Count;

        SaveSettingsRequest request = new() { Name = "qBit", Url = "https://qbit.example" };

        SaveSettingsOutcome outcome = await handler.HandleClientAsync(3, request, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("3");
        configuration.SavedObjects.Should().HaveCount(savesBefore);
    }

    [Fact]
    public async Task HandleIndexerAsync_NegativeIndexFailsCleanlyWithoutPersisting()
    {
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(new TorrentDownloaderSettings());
        int savesBefore = configuration.SavedObjects.Count;

        SaveSettingsRequest request = new() { Name = "Anything", Url = "https://example.local" };

        SaveSettingsOutcome outcome = await handler.HandleIndexerAsync(-1, request, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        configuration.SavedObjects.Should().HaveCount(savesBefore);
    }

    // The two things the indexer/client tests below cover were mutants that survived the
    // whole suite before: making the indexer branch drop every client, and the client
    // branch drop every indexer, changed no test's result. Same hazard as the general
    // form's own data-loss case above - a partial submission overwriting a section it
    // never addressed - one section across.
    [Fact]
    public async Task HandleIndexerAsync_LeavesEveryDownloadClientAndItsSecretIntact()
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

        SaveSettingsRequest request = new() { Name = "Prowlarr", Url = "https://prowlarr.local:9696" };

        SaveSettingsOutcome outcome = await handler.HandleIndexerAsync(0, request, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Clients.Should().HaveCount(2);
        saved.Clients.Should().Contain(client => client.Name == "qBit");
        saved.Clients.Should().Contain(client => client.Name == "Deluge");
        (await secrets.GetAsync("client:qBit:password", CancellationToken.None)).Should().Be("qbit-password");
    }

    [Fact]
    public async Task HandleClientAsync_LeavesEveryIndexerAndItsSecretIntact()
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

        SaveSettingsRequest request = new() { Name = "qBit", Url = "https://qbit.local:8080" };

        SaveSettingsOutcome outcome = await handler.HandleClientAsync(0, request, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Indexers.Should().HaveCount(2);
        saved.Indexers.Should().Contain(indexer => indexer.Name == "Prowlarr");
        saved.Indexers.Should().Contain(indexer => indexer.Name == "Jackett");
        (await secrets.GetAsync("indexer:Prowlarr:apikey", CancellationToken.None)).Should().Be("prowlarr-key");
    }

    [Fact]
    public async Task HandleGeneralAsync_UpdatesCronAndFolders()
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

        await handler.HandleGeneralAsync(request, CancellationToken.None);

        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.TransfersCron.Should().Be("*/2 * * * *");
        saved.IncompleteFolder.Should().Be("/downloads/incomplete");
        saved.IntakeFolder.Should().Be("/downloads/intake");
    }

    // --- private trackers ---------------------------------------------------------

    [Fact]
    public async Task HandleAddPrivateTrackerAsync_AddsAnEntryThatSeedsNothingUntilItIsTold()
    {
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(new TorrentDownloaderSettings());

        await handler.HandleAddPrivateTrackerAsync(CancellationToken.None);

        PrivateTrackerSettings added = ((TorrentDownloaderSettings)configuration.Stored!).PrivateTrackers.Should().ContainSingle().Which;
        added.Name.Should().NotBeNullOrWhiteSpace("two blank names would share one secret key");

        // Adding a tracker is not consenting to upload from it. That is a second,
        // separate decision, and this is the direction it has to fail in.
        added.Seed.Should().BeFalse();
    }

    [Fact]
    public async Task HandlePrivateTrackerAsync_PutsTheAnnounceUrlInTheSecretStoreAndNotInConfiguration()
    {
        (FakeConfiguration configuration, FakeSecretStore secrets, SettingsSaveHandler handler) =
            await SeededAsync(new TorrentDownloaderSettings { PrivateTrackers = [new PrivateTrackerSettings { Name = "RedFish" }] });

        await handler.HandlePrivateTrackerAsync(
            0,
            new SaveSettingsRequest { Name = "RedFish", AnnounceUrl = "https://redfish.test/announce/PASSKEY123", Seed = true },
            CancellationToken.None);

        JsonSerializer.Serialize(configuration.Stored).Should().NotContain("PASSKEY123");
        (await secrets.GetAsync("tracker:RedFish:announce", CancellationToken.None))
            .Should().Be("https://redfish.test/announce/PASSKEY123");
        ((TorrentDownloaderSettings)configuration.Stored!).PrivateTrackers[0].Seed.Should().BeTrue();
    }

    // The form cannot show a stored URL back, so it submits blank when the owner is only
    // changing the ratio. Blank has to mean "leave it" or every unrelated edit wipes the
    // passkey and the tracker silently stops matching anything.
    [Fact]
    public async Task HandlePrivateTrackerAsync_KeepsTheStoredAnnounceUrlWhenTheFormSubmitsNone()
    {
        (_, FakeSecretStore secrets, SettingsSaveHandler handler) =
            await SeededAsync(new TorrentDownloaderSettings { PrivateTrackers = [new PrivateTrackerSettings { Name = "RedFish" }] });
        await secrets.SetAsync("tracker:RedFish:announce", "https://redfish.test/announce/PASSKEY123", CancellationToken.None);

        await handler.HandlePrivateTrackerAsync(
            0,
            new SaveSettingsRequest { Name = "RedFish", SeedRatioTarget = 2.5 },
            CancellationToken.None);

        (await secrets.GetAsync("tracker:RedFish:announce", CancellationToken.None))
            .Should().Be("https://redfish.test/announce/PASSKEY123");
    }

    [Fact]
    public async Task HandlePrivateTrackerAsync_CarriesBothSecretsToTheNewNameOnARename()
    {
        (_, FakeSecretStore secrets, SettingsSaveHandler handler) =
            await SeededAsync(new TorrentDownloaderSettings { PrivateTrackers = [new PrivateTrackerSettings { Name = "RedFish" }] });
        await secrets.SetAsync("tracker:RedFish:announce", "https://redfish.test/a/KEY", CancellationToken.None);
        await secrets.SetAsync("tracker:RedFish:apikey", "torznab-key", CancellationToken.None);

        await handler.HandlePrivateTrackerAsync(
            0,
            new SaveSettingsRequest { Name = "BlueFish" },
            CancellationToken.None);

        (await secrets.GetAsync("tracker:BlueFish:announce", CancellationToken.None)).Should().Be("https://redfish.test/a/KEY");
        (await secrets.GetAsync("tracker:BlueFish:apikey", CancellationToken.None)).Should().Be("torznab-key");
    }

    [Fact]
    public async Task HandlePrivateTrackerAsync_RefusesAnAnnounceUrlThatIsNotAUrlWithoutPersisting()
    {
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) =
            await SeededAsync(new TorrentDownloaderSettings { PrivateTrackers = [new PrivateTrackerSettings { Name = "RedFish" }] });
        int savesBefore = configuration.SavedObjects.Count;

        SaveSettingsOutcome outcome = await handler.HandlePrivateTrackerAsync(
            0,
            new SaveSettingsRequest { Name = "RedFish", AnnounceUrl = "redfish.test/announce" },
            CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        configuration.SavedObjects.Should().HaveCount(savesBefore, "a rejected save must not reach disk");
    }

    // A tracker with no announce URL anywhere matches no torrent, so it can never make
    // one private - and an entry that looks configured but does nothing is worse than one
    // that refuses to be saved.
    [Fact]
    public async Task HandlePrivateTrackerAsync_RefusesToSaveAnEntryThatHasNoAnnounceUrlAtAll()
    {
        (_, _, SettingsSaveHandler handler) =
            await SeededAsync(new TorrentDownloaderSettings { PrivateTrackers = [new PrivateTrackerSettings { Name = "RedFish" }] });

        SaveSettingsOutcome outcome = await handler.HandlePrivateTrackerAsync(
            0,
            new SaveSettingsRequest { Name = "RedFish", Seed = true },
            CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleRemovePrivateTrackerAsync_TakesTheEntryAndItsSecretsWithIt()
    {
        (FakeConfiguration configuration, FakeSecretStore secrets, SettingsSaveHandler handler) =
            await SeededAsync(new TorrentDownloaderSettings { PrivateTrackers = [new PrivateTrackerSettings { Name = "RedFish" }] });
        await secrets.SetAsync("tracker:RedFish:announce", "https://redfish.test/a/KEY", CancellationToken.None);

        await handler.HandleRemovePrivateTrackerAsync(0, CancellationToken.None);

        ((TorrentDownloaderSettings)configuration.Stored!).PrivateTrackers.Should().BeEmpty();
        (await secrets.GetAsync("tracker:RedFish:announce", CancellationToken.None)).Should().BeNull();
    }

    // Every per-entry save rebuilds the settings object, so a field the rebuild forgets is
    // reset to its default by an edit that had nothing to do with it. That is how a toggle
    // turns itself off while the owner is editing an indexer URL, and nothing says so.
    [Fact]
    public async Task HandleIndexerAsync_DoesNotForgetThatSpecialsWereTurnedOn()
    {
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(new TorrentDownloaderSettings
        {
            IncludeSpecials = true,
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.test" }],
        });

        await handler.HandleIndexerAsync(
            0,
            new SaveSettingsRequest { Name = "Prowlarr", Url = "https://prowlarr.test/changed" },
            CancellationToken.None);

        ((TorrentDownloaderSettings)configuration.Stored!).IncludeSpecials.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAddIndexerAsync_DoesNotForgetThatSpecialsWereTurnedOn()
    {
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) =
            await SeededAsync(new TorrentDownloaderSettings { IncludeSpecials = true });

        await handler.HandleAddIndexerAsync(CancellationToken.None);

        ((TorrentDownloaderSettings)configuration.Stored!).IncludeSpecials.Should().BeTrue();
    }

    [Fact]
    public async Task HandleGeneralAsync_TurnsSpecialsOnAndOffAgain()
    {
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(new TorrentDownloaderSettings());

        SaveSettingsRequest on = new()
        {
            TransfersCron = "*/2 * * * *",
            FeedCron = "*/20 * * * *",
            SearchCron = "0 */3 * * *",
            MaintenanceCron = "0 5 * * *",
            IncludeSpecials = true,
        };

        await handler.HandleGeneralAsync(on, CancellationToken.None);
        ((TorrentDownloaderSettings)configuration.Stored!).IncludeSpecials.Should().BeTrue();

        SaveSettingsRequest off = new()
        {
            TransfersCron = "*/2 * * * *",
            FeedCron = "*/20 * * * *",
            SearchCron = "0 */3 * * *",
            MaintenanceCron = "0 5 * * *",
            IncludeSpecials = false,
        };

        // Off again, not just on: a toggle merged with "?? current" can be switched on and
        // never switched off, and the test that only proves the on direction passes anyway.
        await handler.HandleGeneralAsync(off, CancellationToken.None);
        ((TorrentDownloaderSettings)configuration.Stored!).IncludeSpecials.Should().BeFalse();
    }

    // Also the general form's "only its own six fields" contract, since the body used
    // above already carries nothing else - unlike the pre-fix shape, there is no
    // indexerName/clientName field left that could accidentally satisfy this branch.
    [Fact]
    public async Task HandleGeneralAsync_SucceedsWithOnlyItsOwnSixFields()
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

        SaveSettingsOutcome outcome = await handler.HandleGeneralAsync(request, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, "*/1 * * * *", "*/1 * * * *", "*/1 * * * *")]
    [InlineData("", "*/1 * * * *", "*/1 * * * *", "*/1 * * * *")]
    [InlineData("   ", "*/1 * * * *", "*/1 * * * *", "*/1 * * * *")]
    public async Task HandleGeneralAsync_RejectsABlankCronWithoutPersisting(
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

        SaveSettingsOutcome outcome = await handler.HandleGeneralAsync(request, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().NotBeNullOrWhiteSpace();
        configuration.SavedObjects.Should().HaveCount(savesBefore);
    }

    [Fact]
    public async Task HandleIndexerAsync_DoesNotDisturbAnotherIndexersFieldsOrSecret()
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
            Name = "Prowlarr",
            Url = "https://prowlarr.example",
            Priority = 5,
        };

        SaveSettingsOutcome outcome = await handler.HandleIndexerAsync(0, request, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Indexers.Should().ContainSingle(indexer => indexer.Name == "Prowlarr" && indexer.Url == "https://prowlarr.example" && indexer.Priority == 5);
        IndexerSettings jackett = saved.Indexers.Should().ContainSingle(indexer => indexer.Name == "Jackett").Which;
        jackett.Url.Should().Be("https://jackett.local");
        jackett.Priority.Should().Be(10);
        (await secrets.GetAsync("indexer:Jackett:apikey", CancellationToken.None)).Should().Be("jackett-key");
    }

    [Fact]
    public async Task HandleIndexerAsync_EmptySubmittedSecretLeavesStoredSecretInPlace()
    {
        TorrentDownloaderSettings initial = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" }],
        };
        (FakeConfiguration configuration, FakeSecretStore secrets, SettingsSaveHandler handler) =
            await SeededAsync(initial, ("indexer:Prowlarr:apikey", "the-original-key"));

        SaveSettingsRequest request = new()
        {
            Name = "Prowlarr",
            Url = "https://prowlarr.local",
            ApiKey = string.Empty,
        };

        await handler.HandleIndexerAsync(0, request, CancellationToken.None);

        (await secrets.GetAsync("indexer:Prowlarr:apikey", CancellationToken.None)).Should().Be("the-original-key");
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Indexers.Should().ContainSingle(indexer => indexer.Name == "Prowlarr");
    }

    [Fact]
    public async Task HandleIndexerAsync_SubmittedSecretIsWrittenToTheSecretStoreAndNeverToConfiguration()
    {
        TorrentDownloaderSettings initial = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" }],
        };
        (FakeConfiguration configuration, FakeSecretStore secrets, SettingsSaveHandler handler) = await SeededAsync(initial);
        const string apiKey = "brand-new-api-key-that-must-never-leak";

        SaveSettingsRequest request = new()
        {
            Name = "Prowlarr",
            Url = "https://prowlarr.local",
            ApiKey = apiKey,
        };

        await handler.HandleIndexerAsync(0, request, CancellationToken.None);

        (await secrets.GetAsync("indexer:Prowlarr:apikey", CancellationToken.None)).Should().Be(apiKey);
        string serialized = string.Join(
            '\n',
            configuration.SavedObjects.Select(saved => JsonSerializer.Serialize(saved, saved.GetType()))
        );
        serialized.Should().NotContain(apiKey);
    }

    [Fact]
    public async Task HandleIndexerAsync_RejectsANonAbsoluteUrlWithoutPersisting()
    {
        TorrentDownloaderSettings initial = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" }],
        };
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(initial);
        int savesBefore = configuration.SavedObjects.Count;

        SaveSettingsRequest request = new() { Name = "Prowlarr", Url = "not-a-url" };

        SaveSettingsOutcome outcome = await handler.HandleIndexerAsync(0, request, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().NotBeNullOrWhiteSpace();
        configuration.SavedObjects.Should().HaveCount(savesBefore);
    }

    // The chosen behaviour for a rename: honour it, and carry the secret forward to the new
    // name rather than orphaning it. SaveAsync's own key derivation would otherwise silently
    // drop this credential the moment its entry's name changed. The index stays the same
    // across the rename - it identifies the row, not the name - which is what makes the old
    // name recoverable here in the first place.
    [Fact]
    public async Task HandleIndexerAsync_RenamingAnIndexer_PreservesItsSecretUnderTheNewName()
    {
        TorrentDownloaderSettings initial = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" }],
        };
        (FakeConfiguration configuration, FakeSecretStore secrets, SettingsSaveHandler handler) =
            await SeededAsync(initial, ("indexer:Prowlarr:apikey", "the-original-key"));

        SaveSettingsRequest request = new() { Name = "Prowlarr2", Url = "https://prowlarr.local" };

        SaveSettingsOutcome outcome = await handler.HandleIndexerAsync(0, request, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Indexers.Should().ContainSingle(indexer => indexer.Name == "Prowlarr2");
        (await secrets.GetAsync("indexer:Prowlarr2:apikey", CancellationToken.None)).Should().Be("the-original-key");
        (await secrets.GetAsync("indexer:Prowlarr:apikey", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task HandleClientAsync_DoesNotDisturbAnotherClientsFieldsOrSecret()
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
            Name = "qBit",
            Url = "https://qbit.example",
            Username = "changed",
        };

        SaveSettingsOutcome outcome = await handler.HandleClientAsync(0, request, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Clients.Should().ContainSingle(client => client.Name == "qBit" && client.Url == "https://qbit.example" && client.Username == "changed");
        TorrentClientSettings transmission = saved.Clients.Should().ContainSingle(client => client.Name == "Transmission").Which;
        transmission.Url.Should().Be("https://transmission.local");
        transmission.Username.Should().Be("root");
        (await secrets.GetAsync("client:Transmission:password", CancellationToken.None)).Should().Be("transmission-password");
    }

    [Fact]
    public async Task HandleClientAsync_EmptySubmittedPasswordLeavesStoredPasswordInPlace()
    {
        TorrentDownloaderSettings initial = new()
        {
            Clients = [new TorrentClientSettings { Name = "qBit", Url = "https://qbit.local" }],
        };
        (FakeConfiguration configuration, FakeSecretStore secrets, SettingsSaveHandler handler) =
            await SeededAsync(initial, ("client:qBit:password", "the-original-password"));

        SaveSettingsRequest request = new()
        {
            Name = "qBit",
            Url = "https://qbit.local",
            Password = string.Empty,
        };

        await handler.HandleClientAsync(0, request, CancellationToken.None);

        (await secrets.GetAsync("client:qBit:password", CancellationToken.None)).Should().Be("the-original-password");
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Clients.Should().ContainSingle(client => client.Name == "qBit");
    }

    [Fact]
    public async Task HandleClientAsync_RejectsANonAbsoluteUrlWithoutPersisting()
    {
        TorrentDownloaderSettings initial = new()
        {
            Clients = [new TorrentClientSettings { Name = "qBit", Url = "https://qbit.local" }],
        };
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(initial);
        int savesBefore = configuration.SavedObjects.Count;

        SaveSettingsRequest request = new() { Name = "qBit", Url = "not-a-url" };

        SaveSettingsOutcome outcome = await handler.HandleClientAsync(0, request, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        configuration.SavedObjects.Should().HaveCount(savesBefore);
    }

    [Fact]
    public async Task HandleAddIndexerAsync_WhenThereAreNoIndexers_AddsExactlyOne()
    {
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(new TorrentDownloaderSettings());

        SaveSettingsOutcome outcome = await handler.HandleAddIndexerAsync(CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Indexers.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAddClientAsync_WhenThereAreNoClients_AddsExactlyOne()
    {
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(new TorrentDownloaderSettings());

        SaveSettingsOutcome outcome = await handler.HandleAddClientAsync(CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Clients.Should().ContainSingle();
    }

    // The collision task 1 exists to avoid: two blank-named indexers derive the exact same
    // secret key (SettingsGateway.IndexerSecretKey hashes only the name), so a naive "New
    // Indexer" every time would have the second Add's entry silently sharing the first's
    // API key slot the moment either got one.
    [Fact]
    public async Task HandleAddIndexerAsync_CalledTwice_GivesEachEntryAUniqueName()
    {
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(new TorrentDownloaderSettings());

        await handler.HandleAddIndexerAsync(CancellationToken.None);
        await handler.HandleAddIndexerAsync(CancellationToken.None);

        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Indexers.Should().HaveCount(2);
        saved.Indexers.Select(indexer => indexer.Name).Should().OnlyHaveUniqueItems();
        saved.Indexers.Should().OnlyContain(indexer => !string.IsNullOrWhiteSpace(indexer.Name));
    }

    [Fact]
    public async Task HandleAddClientAsync_CalledTwice_GivesEachEntryAUniqueName()
    {
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(new TorrentDownloaderSettings());

        await handler.HandleAddClientAsync(CancellationToken.None);
        await handler.HandleAddClientAsync(CancellationToken.None);

        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Clients.Should().HaveCount(2);
        saved.Clients.Select(client => client.Name).Should().OnlyHaveUniqueItems();
        saved.Clients.Should().OnlyContain(client => !string.IsNullOrWhiteSpace(client.Name));
    }

    [Fact]
    public async Task HandleAddIndexerAsync_LeavesEveryDownloadClientAndItsSecretIntact()
    {
        TorrentDownloaderSettings initial = new()
        {
            Clients = [new TorrentClientSettings { Name = "qBit", Url = "https://qbit.local" }],
        };
        (FakeConfiguration configuration, FakeSecretStore secrets, SettingsSaveHandler handler) =
            await SeededAsync(initial, ("client:qBit:password", "qbit-password"));

        SaveSettingsOutcome outcome = await handler.HandleAddIndexerAsync(CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Clients.Should().ContainSingle(client => client.Name == "qBit");
        (await secrets.GetAsync("client:qBit:password", CancellationToken.None)).Should().Be("qbit-password");
    }

    [Fact]
    public async Task HandleAddClientAsync_LeavesEveryIndexerAndItsSecretIntact()
    {
        TorrentDownloaderSettings initial = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" }],
        };
        (FakeConfiguration configuration, FakeSecretStore secrets, SettingsSaveHandler handler) =
            await SeededAsync(initial, ("indexer:Prowlarr:apikey", "prowlarr-key"));

        SaveSettingsOutcome outcome = await handler.HandleAddClientAsync(CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Indexers.Should().ContainSingle(indexer => indexer.Name == "Prowlarr");
        (await secrets.GetAsync("indexer:Prowlarr:apikey", CancellationToken.None)).Should().Be("prowlarr-key");
    }

    // THE hazard test for removal: SaveAsync's orphan sweep is what actually deletes a
    // secret whose entry is gone, and this is what proves that rather than assuming it. Red
    // run: pointed at a stand-in HandleRemoveIndexerAsync that dropped the entry in memory
    // but never called gateway.SaveAsync (a realistic near-miss - an early return, or a
    // forgotten await), it failed on the FIRST assertion, "saved.Indexers to be empty", not
    // the secret one - configuration.Stored was never touched, so the removed entry was
    // still sitting right there. That is exactly the failure this test exists to catch:
    // persisting nothing reads as nothing removed, secret included.
    [Fact]
    public async Task HandleRemoveIndexerAsync_DeletesTheEntrysStoredSecret()
    {
        TorrentDownloaderSettings initial = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" }],
        };
        (FakeConfiguration configuration, FakeSecretStore secrets, SettingsSaveHandler handler) =
            await SeededAsync(initial, ("indexer:Prowlarr:apikey", "the-api-key"));

        SaveSettingsOutcome outcome = await handler.HandleRemoveIndexerAsync(0, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Indexers.Should().BeEmpty();
        (await secrets.GetAsync("indexer:Prowlarr:apikey", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task HandleRemoveClientAsync_DeletesTheEntrysStoredSecret()
    {
        TorrentDownloaderSettings initial = new()
        {
            Clients = [new TorrentClientSettings { Name = "qBit", Url = "https://qbit.local" }],
        };
        (FakeConfiguration configuration, FakeSecretStore secrets, SettingsSaveHandler handler) =
            await SeededAsync(initial, ("client:qBit:password", "the-password"));

        SaveSettingsOutcome outcome = await handler.HandleRemoveClientAsync(0, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Clients.Should().BeEmpty();
        (await secrets.GetAsync("client:qBit:password", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task HandleRemoveIndexerAsync_RemovingTheOnlyIndexer_LeavesZeroIndexers()
    {
        TorrentDownloaderSettings initial = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" }],
        };
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(initial);

        await handler.HandleRemoveIndexerAsync(0, CancellationToken.None);

        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Indexers.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleRemoveIndexerAsync_LeavesEveryOtherIndexerAndItsSecretIntact()
    {
        TorrentDownloaderSettings initial = new()
        {
            Indexers =
            [
                new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" },
                new IndexerSettings { Name = "Jackett", Url = "https://jackett.local" },
            ],
        };
        (FakeConfiguration configuration, FakeSecretStore secrets, SettingsSaveHandler handler) =
            await SeededAsync(initial, ("indexer:Jackett:apikey", "jackett-key"));

        SaveSettingsOutcome outcome = await handler.HandleRemoveIndexerAsync(0, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Indexers.Should().ContainSingle(indexer => indexer.Name == "Jackett");
        (await secrets.GetAsync("indexer:Jackett:apikey", CancellationToken.None)).Should().Be("jackett-key");
    }

    [Fact]
    public async Task HandleRemoveIndexerAsync_LeavesEveryDownloadClientAndItsSecretIntact()
    {
        TorrentDownloaderSettings initial = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" }],
            Clients = [new TorrentClientSettings { Name = "qBit", Url = "https://qbit.local" }],
        };
        (FakeConfiguration configuration, FakeSecretStore secrets, SettingsSaveHandler handler) =
            await SeededAsync(initial, ("client:qBit:password", "qbit-password"));

        SaveSettingsOutcome outcome = await handler.HandleRemoveIndexerAsync(0, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Clients.Should().ContainSingle(client => client.Name == "qBit");
        (await secrets.GetAsync("client:qBit:password", CancellationToken.None)).Should().Be("qbit-password");
    }

    [Fact]
    public async Task HandleRemoveClientAsync_LeavesEveryIndexerAndItsSecretIntact()
    {
        TorrentDownloaderSettings initial = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" }],
            Clients = [new TorrentClientSettings { Name = "qBit", Url = "https://qbit.local" }],
        };
        (FakeConfiguration configuration, FakeSecretStore secrets, SettingsSaveHandler handler) =
            await SeededAsync(initial, ("indexer:Prowlarr:apikey", "prowlarr-key"));

        SaveSettingsOutcome outcome = await handler.HandleRemoveClientAsync(0, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.Indexers.Should().ContainSingle(indexer => indexer.Name == "Prowlarr");
        (await secrets.GetAsync("indexer:Prowlarr:apikey", CancellationToken.None)).Should().Be("prowlarr-key");
    }

    [Fact]
    public async Task HandleRemoveIndexerAsync_OutOfRangeIndexFailsCleanlyWithoutPersisting()
    {
        TorrentDownloaderSettings initial = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" }],
        };
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(initial);
        int savesBefore = configuration.SavedObjects.Count;

        SaveSettingsOutcome outcome = await handler.HandleRemoveIndexerAsync(4, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("4");
        configuration.SavedObjects.Should().HaveCount(savesBefore);
    }

    [Fact]
    public async Task HandleRemoveClientAsync_OutOfRangeIndexFailsCleanlyWithoutPersisting()
    {
        TorrentDownloaderSettings initial = new()
        {
            Clients = [new TorrentClientSettings { Name = "qBit", Url = "https://qbit.local" }],
        };
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(initial);
        int savesBefore = configuration.SavedObjects.Count;

        SaveSettingsOutcome outcome = await handler.HandleRemoveClientAsync(3, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("3");
        configuration.SavedObjects.Should().HaveCount(savesBefore);
    }

    [Fact]
    public async Task HandleRemoveIndexerAsync_NegativeIndexFailsCleanlyWithoutPersisting()
    {
        (FakeConfiguration configuration, _, SettingsSaveHandler handler) = await SeededAsync(new TorrentDownloaderSettings());
        int savesBefore = configuration.SavedObjects.Count;

        SaveSettingsOutcome outcome = await handler.HandleRemoveIndexerAsync(-1, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        configuration.SavedObjects.Should().HaveCount(savesBefore);
    }

    [Fact]
    public async Task HandleGeneralAsync_OnSuccess_StampsLastSavedAtUtcFromTheInjectedClock()
    {
        FakeConfiguration configuration = new();
        FakeSecretStore secretStore = new();
        SettingsGateway gateway = new(configuration, secretStore);
        DateTimeOffset now = new(2026, 7, 31, 1, 59, 0, TimeSpan.Zero);
        FakeClock clock = new(now);
        SettingsSaveHandler handler = new(gateway, clock);

        await handler.HandleGeneralAsync(
            new SaveSettingsRequest
            {
                TransfersCron = "*/2 * * * *",
                FeedCron = "*/20 * * * *",
                SearchCron = "0 */3 * * *",
                MaintenanceCron = "0 5 * * *",
            },
            CancellationToken.None
        );

        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.LastSavedAtUtc.Should().Be(now);
    }

    [Fact]
    public async Task HandleAddIndexerAsync_OnSuccess_StampsLastSavedAtUtcFromTheInjectedClock()
    {
        FakeConfiguration configuration = new();
        FakeSecretStore secretStore = new();
        SettingsGateway gateway = new(configuration, secretStore);
        DateTimeOffset now = new(2026, 7, 31, 1, 59, 0, TimeSpan.Zero);
        FakeClock clock = new(now);
        SettingsSaveHandler handler = new(gateway, clock);

        await handler.HandleAddIndexerAsync(CancellationToken.None);

        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.LastSavedAtUtc.Should().Be(now);
    }

    [Fact]
    public async Task HandleIndexerAsync_WhenValidationFails_LeavesLastSavedAtUtcUntouched()
    {
        TorrentDownloaderSettings initial = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" }],
            LastSavedAtUtc = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        FakeConfiguration configuration = new();
        FakeSecretStore secretStore = new();
        SettingsGateway gateway = new(configuration, secretStore);
        await gateway.SaveAsync(new LoadedSettings(initial, [], []), CancellationToken.None);
        FakeClock clock = new(new DateTimeOffset(2026, 7, 31, 1, 59, 0, TimeSpan.Zero));
        SettingsSaveHandler handler = new(gateway, clock);

        SaveSettingsOutcome outcome = await handler.HandleIndexerAsync(
            0,
            new SaveSettingsRequest { Name = "Prowlarr", Url = "not-a-url" },
            CancellationToken.None
        );

        outcome.Succeeded.Should().BeFalse();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)configuration.Stored!;
        saved.LastSavedAtUtc.Should().Be(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }
}

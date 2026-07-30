// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Configuration;

// SettingsView renders three kinds of form - general, one per indexer, one per client -
// and each posts here as its own SaveSettings call. SaveAsync treats whatever LoadedSettings
// it is handed as the complete world: it writes the whole settings object to configuration,
// and it deletes every stored secret whose entry is not present in what it was handed. A
// form only ever carries part of that world, so this class' entire job is to load the
// current one, apply just the part this submission addresses, and hand SaveAsync the
// merged result - never the submitted fields alone. Reconstructing settings from the
// submitted fields and saving that directly would wipe every indexer, every client and
// every stored credential the moment the owner saves the general form.
public sealed class SettingsSaveHandler(SettingsGateway gateway)
{
    public async Task<SaveSettingsOutcome> HandleAsync(SaveSettingsRequest request, CancellationToken ct = default)
    {
        LoadedSettings current = await gateway.LoadAsync(ct);

        SaveSettingsOutcome outcome = request.IndexerName is not null
            ? ApplyIndexer(current, request)
            : request.ClientName is not null
                ? ApplyClient(current, request)
                : ApplyGeneral(current, request);

        if (!outcome.Succeeded)
        {
            return outcome;
        }

        await gateway.SaveAsync(outcome.Merged!, ct);
        return outcome;
    }

    // No secret changes hands here: none of the four cron fields or the two folders is a
    // secret, and every existing indexer/client - and, critically, every secret already
    // stored for them - carries forward untouched because their names are unchanged in the
    // merged settings. SaveAsync's orphan sweep only deletes a secret whose entry's name is
    // absent from what it is handed, so leaving the lists as loaded is what keeps them safe,
    // not an empty-secrets list here (that would only matter if a name had actually changed).
    private static SaveSettingsOutcome ApplyGeneral(LoadedSettings current, SaveSettingsRequest request)
    {
        (string Field, string? Value)[] crons =
        [
            ("Transfers schedule", request.TransfersCron),
            ("Feed schedule", request.FeedCron),
            ("Search schedule", request.SearchCron),
            ("Maintenance schedule", request.MaintenanceCron),
        ];

        foreach ((string field, string? value) in crons)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return SaveSettingsOutcome.Failure($"{field} cannot be blank.");
            }
        }

        TorrentDownloaderSettings merged = new()
        {
            TransfersCron = request.TransfersCron!,
            FeedCron = request.FeedCron!,
            SearchCron = request.SearchCron!,
            MaintenanceCron = request.MaintenanceCron!,
            IncompleteFolder = request.IncompleteFolder ?? string.Empty,
            IntakeFolder = request.IntakeFolder ?? string.Empty,
            Indexers = current.Settings.Indexers,
            Clients = current.Settings.Clients,
        };

        return SaveSettingsOutcome.Success(new LoadedSettings(merged, [], []));
    }

    // A rename (name != IndexerName, the identity the form was built with) is honoured
    // rather than refused: the entry moves to a new key under SaveAsync's orphan sweep
    // because the key derives from the name, so the one deliberate step here is carrying
    // the secret forward to the NEW name whenever the owner did not also submit a new one -
    // otherwise the old key falls out of the live set and SaveAsync deletes it, silently
    // losing the credential on what looked like an ordinary edit.
    private static SaveSettingsOutcome ApplyIndexer(LoadedSettings current, SaveSettingsRequest request)
    {
        string oldName = request.IndexerName!;
        int index = current.Settings.Indexers.FindIndex(indexer => indexer.Name == oldName);

        if (index < 0)
        {
            return SaveSettingsOutcome.Failure($"Indexer '{oldName}' was not found.");
        }

        if (!IsAbsoluteUrl(request.Url))
        {
            return SaveSettingsOutcome.Failure("Indexer URL must be an absolute URL.");
        }

        IndexerSettings existing = current.Settings.Indexers[index];
        string newName = string.IsNullOrWhiteSpace(request.Name) ? oldName : request.Name!;

        IndexerSettings updated = new()
        {
            Name = newName,
            Kind = request.Kind ?? existing.Kind,
            Url = request.Url!,
            Priority = request.Priority ?? existing.Priority,
            Enabled = request.Enabled ?? existing.Enabled,
            MinimumIntervalSeconds = request.MinimumIntervalSeconds ?? existing.MinimumIntervalSeconds,
            Categories = ParseCategories(request.Categories),
        };

        List<IndexerSettings> indexers = [.. current.Settings.Indexers];
        indexers[index] = updated;

        TorrentDownloaderSettings merged = CloneWithoutEntries(current.Settings);
        merged.Indexers = indexers;
        merged.Clients = current.Settings.Clients;

        string? apiKey = string.IsNullOrEmpty(request.ApiKey)
            ? current.IndexerSecrets.FirstOrDefault(secret => secret.Name == oldName)?.ApiKey
            : request.ApiKey;

        List<IndexerSecret> indexerSecrets = string.IsNullOrEmpty(apiKey) ? [] : [new IndexerSecret(newName, apiKey)];

        return SaveSettingsOutcome.Success(new LoadedSettings(merged, indexerSecrets, []));
    }

    private static SaveSettingsOutcome ApplyClient(LoadedSettings current, SaveSettingsRequest request)
    {
        string oldName = request.ClientName!;
        int index = current.Settings.Clients.FindIndex(client => client.Name == oldName);

        if (index < 0)
        {
            return SaveSettingsOutcome.Failure($"Download client '{oldName}' was not found.");
        }

        if (!IsAbsoluteUrl(request.Url))
        {
            return SaveSettingsOutcome.Failure("Download client URL must be an absolute URL.");
        }

        TorrentClientSettings existing = current.Settings.Clients[index];
        string newName = string.IsNullOrWhiteSpace(request.Name) ? oldName : request.Name!;

        TorrentClientSettings updated = new()
        {
            Name = newName,
            Kind = request.Kind ?? existing.Kind,
            Url = request.Url!,
            Username = request.Username ?? existing.Username,
            Enabled = request.Enabled ?? existing.Enabled,
        };

        List<TorrentClientSettings> clients = [.. current.Settings.Clients];
        clients[index] = updated;

        TorrentDownloaderSettings merged = CloneWithoutEntries(current.Settings);
        merged.Indexers = current.Settings.Indexers;
        merged.Clients = clients;

        string? password = string.IsNullOrEmpty(request.Password)
            ? current.ClientSecrets.FirstOrDefault(secret => secret.Name == oldName)?.Password
            : request.Password;

        List<ClientSecret> clientSecrets = string.IsNullOrEmpty(password) ? [] : [new ClientSecret(newName, password)];

        return SaveSettingsOutcome.Success(new LoadedSettings(merged, [], clientSecrets));
    }

    private static bool IsAbsoluteUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out _);

    private static List<string> ParseCategories(string? categories) =>
        string.IsNullOrWhiteSpace(categories)
            ? []
            : [.. categories.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    // Copies everything except Indexers/Clients, which each caller sets itself - the
    // general form's fields have no place here since only one of the three forms is ever
    // being applied at a time.
    private static TorrentDownloaderSettings CloneWithoutEntries(TorrentDownloaderSettings source) =>
        new()
        {
            TransfersCron = source.TransfersCron,
            FeedCron = source.FeedCron,
            SearchCron = source.SearchCron,
            MaintenanceCron = source.MaintenanceCron,
            IncompleteFolder = source.IncompleteFolder,
            IntakeFolder = source.IntakeFolder,
        };
}

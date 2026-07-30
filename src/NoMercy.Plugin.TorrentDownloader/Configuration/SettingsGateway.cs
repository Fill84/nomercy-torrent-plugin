// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Configuration;

// The transport for a secret in flight between the gateway and its caller. It never
// lives on IndexerSettings/TorrentClientSettings: those are the objects handed to
// IPluginConfiguration.SaveConfigurationAsync, and anything serialised there lands in
// plaintext JSON on disk.
public sealed record IndexerSecret(string Name, string ApiKey);

public sealed record ClientSecret(string Name, string Password);

// LoadAsync's result and SaveAsync's input: the secret-free settings plus each entry's
// secret carried alongside, keyed by the entry's Name.
public sealed record LoadedSettings(
    TorrentDownloaderSettings Settings,
    IReadOnlyList<IndexerSecret> IndexerSecrets,
    IReadOnlyList<ClientSecret> ClientSecrets
);

// Keeps IPluginConfiguration (whole-object JSON on disk) and IPluginSecretStore
// (protected storage) apart. A secret must never reach the configuration object; this
// is the only place in the plugin allowed to see both stores at once.
public sealed class SettingsGateway(IPluginConfiguration configuration, IPluginSecretStore secretStore)
{
    private const string IndexerKind = "indexer";
    private const string ClientKind = "client";

    public async Task<LoadedSettings> LoadAsync(CancellationToken ct = default)
    {
        TorrentDownloaderSettings settings = configuration.HasConfiguration()
            ? await configuration.GetConfigurationAsync<TorrentDownloaderSettings>(ct) ?? new TorrentDownloaderSettings()
            : new TorrentDownloaderSettings();

        List<IndexerSecret> indexerSecrets = [];
        foreach (IndexerSettings indexer in settings.Indexers)
        {
            string? apiKey = await secretStore.GetAsync(SecretKeyFor(IndexerKind, indexer.Name), ct);
            if (!string.IsNullOrEmpty(apiKey))
            {
                indexerSecrets.Add(new IndexerSecret(indexer.Name, apiKey));
            }
        }

        List<ClientSecret> clientSecrets = [];
        foreach (TorrentClientSettings client in settings.Clients)
        {
            string? password = await secretStore.GetAsync(SecretKeyFor(ClientKind, client.Name), ct);
            if (!string.IsNullOrEmpty(password))
            {
                clientSecrets.Add(new ClientSecret(client.Name, password));
            }
        }

        return new LoadedSettings(settings, indexerSecrets, clientSecrets);
    }

    public async Task SaveAsync(LoadedSettings settings, CancellationToken ct = default)
    {
        await configuration.SaveConfigurationAsync(settings.Settings, ct);

        HashSet<string> liveKeys =
        [
            .. settings.Settings.Indexers.Select(indexer => SecretKeyFor(IndexerKind, indexer.Name)),
            .. settings.Settings.Clients.Select(client => SecretKeyFor(ClientKind, client.Name)),
        ];

        IReadOnlyList<string> storedKeys = await secretStore.KeysAsync(ct);
        foreach (string key in storedKeys)
        {
            bool ownedByThisSettingsShape =
                key.StartsWith(IndexerKind + ":", StringComparison.Ordinal)
                || key.StartsWith(ClientKind + ":", StringComparison.Ordinal);

            if (ownedByThisSettingsShape && !liveKeys.Contains(key))
            {
                await secretStore.DeleteAsync(key, ct);
            }
        }

        foreach (IndexerSecret secret in settings.IndexerSecrets)
        {
            if (!string.IsNullOrEmpty(secret.ApiKey))
            {
                await secretStore.SetAsync(SecretKeyFor(IndexerKind, secret.Name), secret.ApiKey, ct);
            }
        }

        foreach (ClientSecret secret in settings.ClientSecrets)
        {
            if (!string.IsNullOrEmpty(secret.Password))
            {
                await secretStore.SetAsync(SecretKeyFor(ClientKind, secret.Name), secret.Password, ct);
            }
        }
    }

    private static string SecretKeyFor(string kind, string name)
    {
        string field = kind switch
        {
            IndexerKind => "apikey",
            ClientKind => "password",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown secret kind."),
        };

        return $"{kind}:{name}:{field}";
    }
}

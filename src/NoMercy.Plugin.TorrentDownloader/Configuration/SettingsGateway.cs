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

// Two fields rather than one, because a private tracker's announce URL is itself the
// secret: it carries the passkey that identifies the account. ApiKey is separate and
// optional - some trackers want one for the Torznab search path and some do not.
public sealed record PrivateTrackerSecret(string Name, string AnnounceUrl, string? ApiKey);

// LoadAsync's result and SaveAsync's input: the secret-free settings plus each entry's
// secret carried alongside, keyed by the entry's Name.
public sealed record LoadedSettings(
    TorrentDownloaderSettings Settings,
    IReadOnlyList<IndexerSecret> IndexerSecrets,
    IReadOnlyList<ClientSecret> ClientSecrets,
    IReadOnlyList<PrivateTrackerSecret> PrivateTrackerSecrets
)
{
    /// <summary>The shape for an operation that touches no private tracker, which is most of them.</summary>
    public LoadedSettings(
        TorrentDownloaderSettings settings,
        IReadOnlyList<IndexerSecret> indexerSecrets,
        IReadOnlyList<ClientSecret> clientSecrets
    )
        : this(settings, indexerSecrets, clientSecrets, [])
    {
    }
}

// Keeps IPluginConfiguration (whole-object JSON on disk) and IPluginSecretStore
// (protected storage) apart. A secret must never reach the configuration object; this
// is the only place in the plugin allowed to see both stores at once.
public sealed class SettingsGateway(IPluginConfiguration configuration, IPluginSecretStore secretStore)
{
    private const string IndexerKind = "indexer";
    private const string ClientKind = "client";
    private const string TrackerKind = "tracker";

    // Exposed so SettingsView can ask "is this entry's secret stored?" against the exact
    // key LoadAsync/SaveAsync use, rather than a second copy of the kind:name:field format
    // drifting out of step with this one.
    public static string IndexerSecretKey(string name) => SecretKeyFor(IndexerKind, name, "apikey");

    public static string ClientSecretKey(string name) => SecretKeyFor(ClientKind, name, "password");

    // A private tracker owns two keys where the others own one, which is why the field is
    // a parameter now instead of being derived from the kind.
    public static string PrivateTrackerAnnounceKey(string name) => SecretKeyFor(TrackerKind, name, "announce");

    public static string PrivateTrackerApiKeyKey(string name) => SecretKeyFor(TrackerKind, name, "apikey");

    public async Task<LoadedSettings> LoadAsync(CancellationToken ct = default)
    {
        TorrentDownloaderSettings settings = configuration.HasConfiguration()
            ? await configuration.GetConfigurationAsync<TorrentDownloaderSettings>(ct) ?? new TorrentDownloaderSettings()
            : new TorrentDownloaderSettings();

        List<IndexerSecret> indexerSecrets = [];
        foreach (IndexerSettings indexer in settings.Indexers)
        {
            string? apiKey = await secretStore.GetAsync(IndexerSecretKey(indexer.Name), ct);
            if (!string.IsNullOrEmpty(apiKey))
            {
                indexerSecrets.Add(new IndexerSecret(indexer.Name, apiKey));
            }
        }

        List<ClientSecret> clientSecrets = [];
        foreach (TorrentClientSettings client in settings.Clients)
        {
            string? password = await secretStore.GetAsync(ClientSecretKey(client.Name), ct);
            if (!string.IsNullOrEmpty(password))
            {
                clientSecrets.Add(new ClientSecret(client.Name, password));
            }
        }

        List<PrivateTrackerSecret> trackerSecrets = [];
        foreach (PrivateTrackerSettings tracker in settings.PrivateTrackers)
        {
            string? announce = await secretStore.GetAsync(PrivateTrackerAnnounceKey(tracker.Name), ct);

            // No announce URL means no tracker: the host it announces to is the whole of
            // its identity, so an entry without one cannot match a torrent to anything
            // and must not be handed on as though it could.
            if (string.IsNullOrEmpty(announce))
                continue;

            string? apiKey = await secretStore.GetAsync(PrivateTrackerApiKeyKey(tracker.Name), ct);

            trackerSecrets.Add(new PrivateTrackerSecret(tracker.Name, announce, string.IsNullOrEmpty(apiKey) ? null : apiKey));
        }

        return new LoadedSettings(settings, indexerSecrets, clientSecrets, trackerSecrets);
    }

    public async Task SaveAsync(LoadedSettings settings, CancellationToken ct = default)
    {
        await configuration.SaveConfigurationAsync(settings.Settings, ct);

        HashSet<string> liveKeys =
        [
            .. settings.Settings.Indexers.Select(indexer => IndexerSecretKey(indexer.Name)),
            .. settings.Settings.Clients.Select(client => ClientSecretKey(client.Name)),

            // Both of a tracker's keys, or the sweep below would delete the API key of
            // every tracker that has one on the next save.
            .. settings.Settings.PrivateTrackers.Select(tracker => PrivateTrackerAnnounceKey(tracker.Name)),
            .. settings.Settings.PrivateTrackers.Select(tracker => PrivateTrackerApiKeyKey(tracker.Name)),
        ];

        IReadOnlyList<string> storedKeys = await secretStore.KeysAsync(ct);
        foreach (string key in storedKeys)
        {
            bool ownedByThisSettingsShape =
                key.StartsWith(IndexerKind + ":", StringComparison.Ordinal)
                || key.StartsWith(ClientKind + ":", StringComparison.Ordinal)
                || key.StartsWith(TrackerKind + ":", StringComparison.Ordinal);

            if (ownedByThisSettingsShape && !liveKeys.Contains(key))
            {
                await secretStore.DeleteAsync(key, ct);
            }
        }

        foreach (IndexerSecret secret in settings.IndexerSecrets)
        {
            if (!string.IsNullOrEmpty(secret.ApiKey))
            {
                await secretStore.SetAsync(IndexerSecretKey(secret.Name), secret.ApiKey, ct);
            }
        }

        foreach (ClientSecret secret in settings.ClientSecrets)
        {
            if (!string.IsNullOrEmpty(secret.Password))
            {
                await secretStore.SetAsync(ClientSecretKey(secret.Name), secret.Password, ct);
            }
        }

        foreach (PrivateTrackerSecret secret in settings.PrivateTrackerSecrets)
        {
            if (!string.IsNullOrEmpty(secret.AnnounceUrl))
            {
                await secretStore.SetAsync(PrivateTrackerAnnounceKey(secret.Name), secret.AnnounceUrl, ct);
            }

            if (!string.IsNullOrEmpty(secret.ApiKey))
            {
                await secretStore.SetAsync(PrivateTrackerApiKeyKey(secret.Name), secret.ApiKey, ct);
            }
        }
    }

    private static string SecretKeyFor(string kind, string name, string field) => $"{kind}:{name}:{field}";
}

using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Configuration;

/// <summary>
/// Reads and writes the settings, and refuses the ones that would not work.
/// </summary>
/// <remarks>
/// Validation lives here rather than on the page, because the endpoint and the
/// page are two ways into the same save and a rule enforced in only one of them
/// is a rule the other way round it.
/// </remarks>
public sealed class SettingsStore
{
    private readonly IPluginConfiguration _configuration;
    private readonly IPluginSecretStore _secrets;
    private readonly Func<string, string?> _volumeOf;

    /// <summary>
    /// <c>volumeOf</c> answers which volume a path is on. It is a seam because
    /// the alternative is a test that passes or fails on how many drives the
    /// machine running it happens to have.
    /// </summary>
    public SettingsStore(
        IPluginConfiguration configuration,
        IPluginSecretStore secrets,
        Func<string, string?>? volumeOf = null)
    {
        _configuration = configuration;
        _secrets = secrets;
        _volumeOf = volumeOf ?? Path.GetPathRoot;
    }

    /// <summary>Where an indexer's API key is kept.</summary>
    public static string IndexerApiKey(string indexerId)
    {
        return $"indexer:{indexerId}:apikey";
    }

    /// <summary>Where a private tracker's passkey is kept.</summary>
    public static string TrackerPasskey(string trackerId)
    {
        return $"tracker:{trackerId}:passkey";
    }

    /// <summary>
    /// The stored settings, or the documented defaults when nothing was ever
    /// saved — never a half-filled object the caller has to know the defaults
    /// for.
    /// </summary>
    public async Task<Settings> LoadAsync(CancellationToken ct)
    {
        return await _configuration.GetConfigurationAsync<Settings>(ct) ?? new Settings();
    }

    /// <summary>
    /// Checks <paramref name="settings"/> and writes them only if every check
    /// passed.
    /// </summary>
    public async Task<SaveResult> SaveAsync(Settings settings, CancellationToken ct)
    {
        List<string> errors = [];
        List<string> warnings = [];

        foreach ((string name, string expression) in settings.Cadences.All())
        {
            if (!Cron.IsValid(expression, out string? reason))
            {
                errors.Add($"The {name} cadence '{expression}' is not a cron. {reason}");
            }
        }

        CheckFolder("incomplete", settings.IncompleteFolder, errors);
        CheckFolder("intake", settings.IntakeFolder, errors);

        if (errors.Count > 0)
        {
            // Nothing written. The stored settings stay exactly as they were.
            return new(false, errors, warnings);
        }

        if (OnDifferentVolumes(settings.IncompleteFolder, settings.IntakeFolder))
        {
            // Not a refusal: it is a working setup somebody may well have meant
            // — a fast disk to download onto, a large one to keep. It costs a
            // full-file copy on every completion, which is worth knowing before
            // the first one rather than after.
            warnings.Add(
                "The incomplete and intake folders are on different volumes, so every finished download pays a full-file copy instead of a rename.");
        }

        await _configuration.SaveConfigurationAsync(settings, ct);

        return new(true, errors, warnings);
    }

    /// <summary>Stores a secret. It never travels through <see cref="Settings"/>.</summary>
    public Task SetSecretAsync(string key, string value, CancellationToken ct)
    {
        return _secrets.SetAsync(key, value, ct);
    }

    public Task ForgetSecretAsync(string key, CancellationToken ct)
    {
        return _secrets.DeleteAsync(key, ct);
    }

    /// <summary>
    /// Which secrets exist. Names only — this is what the page is handed, and
    /// it is the reason the page cannot show a value even by accident.
    /// </summary>
    public async Task<IReadOnlyList<string>> SecretsSetAsync(CancellationToken ct)
    {
        return await _secrets.KeysAsync(ct);
    }

    /// <summary>
    /// Whether two paths are on different volumes.
    /// </summary>
    /// <remarks>
    /// Two empty paths are not two volumes: an unconfigured plugin should not
    /// greet the owner with a warning about folders they have not chosen.
    /// </remarks>
    public bool OnDifferentVolumes(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        return !string.Equals(_volumeOf(first), _volumeOf(second), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a folder can be written, found out by writing to it.
    /// </summary>
    /// <remarks>
    /// Existence is not permission and neither is an attribute: a share that
    /// has gone read-only, a full disk and a folder owned by another user all
    /// look fine until something is written. The alternative is finding out at
    /// three in the morning, when a finished transfer has nowhere to go.
    /// </remarks>
    private void CheckFolder(string which, string path, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add($"The {which} folder has not been chosen.");
            return;
        }

        try
        {
            Directory.CreateDirectory(path);

            string probe = Path.Combine(path, $".nomercy-write-test-{Guid.NewGuid():n}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException or NotSupportedException)
        {
            errors.Add($"The {which} folder '{path}' cannot be written: {exception.Message}");
        }
    }
}

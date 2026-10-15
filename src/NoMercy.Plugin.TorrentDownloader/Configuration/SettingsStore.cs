using System.Text.Json;

using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.PluginSdk.Abstractions;

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
    /// What the host last said the settings were, as JSON, or nothing when it has
    /// not been asked since the last save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The host's read is a file check, a full file read and a deserialise,
    /// behind a lock it shares with every other plugin on the server — and every
    /// transfers pass, every cycle and every page the owner opens asks for the
    /// settings.
    /// </para>
    /// <para>
    /// A load that began before a save must not be remembered after it: the
    /// answer it is carrying is the one the save replaced, and a settings cache
    /// holding what the owner has just changed away from is worse than the
    /// round trip it saves. So every save puts a new, empty one here, and a load
    /// keeps its answer only by swapping out the very one it saw when it began —
    /// one step, which a save cannot land in the middle of. Checking first and
    /// keeping after was two steps, and a save between them was overwritten by
    /// the answer it replaced.
    /// </para>
    /// </remarks>
    private Remembered _remembered = new(null);

    /// <summary>One answer of the host's, or none; compared by which one it is, never by what it holds.</summary>
    private sealed class Remembered(string? json)
    {
        public string? Json { get; } = json;
    }

    /// <summary>The settings, with the two seams the tests need.</summary>
    /// <param name="configuration">Where the settings are kept.</param>
    /// <param name="secrets">Where a passkey is kept, which is never here.</param>
    /// <param name="volumeOf">
    /// Which volume a path is on. It is a seam because the alternative is a
    /// test that passes or fails on how many drives the machine running it
    /// happens to have.
    /// </param>
    /// <param name="places">
    /// The server's own list of places it can write. media-server #32, which
    /// this plugin opened, names this exact case: the intake folder is a string
    /// the owner typed, on whatever machine the server happens to be. The list
    /// cannot replace <see cref="CheckFolder"/> — that one creates the folder
    /// and writes a real file into it, which proves more than any list can —
    /// but it can say where the server <em>can</em> write when the typed path
    /// turns out to be somewhere it cannot. A refusal the owner can act on
    /// rather than one they can only read.
    ///
    /// On contract 12 it is <c>IPluginServerInfo.GrantedPaths</c>, read by the
    /// caller at the moment it is wanted; a server that wires no server-info
    /// facade answers nothing, and the refusal is then what it always was.
    /// </param>
    /// <param name="remembering">
    /// Called by a load that has read the host's answer, just before it keeps it. Nothing on a server passes
    /// it. It is a seam because the one interleaving that matters — a save landing between a load's read and
    /// its keeping what it read — is a few instructions wide, and a test that waited for it by chance would
    /// pass whether or not the store got it right.
    /// </param>
    public SettingsStore(
        IPluginConfiguration configuration,
        IPluginSecretStore secrets,
        Func<string, string?>? volumeOf = null,
        Func<IReadOnlyList<PluginStorageLocation>>? places = null,
        Func<Task>? remembering = null)
    {
        _configuration = configuration;
        _secrets = secrets;
        _volumeOf = volumeOf ?? Path.GetPathRoot;
        _places = places;
        _remembering = remembering;
    }

    /// <summary>See the constructor's <c>remembering</c>.</summary>
    private readonly Func<Task>? _remembering;

    /// <summary>How to reach the server's own list of places it can write.</summary>
    /// <remarks>
    /// Asked for at the moment it is wanted, which is only ever a folder that
    /// was refused. Resolved when the plugin starts instead, it would make
    /// every start depend on a facade the server need not wire at all.
    /// </remarks>
    private readonly Func<IReadOnlyList<PluginStorageLocation>>? _places;

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
        string settings;
        Remembered seen = Volatile.Read(ref _remembered);

        if (seen.Json is string remembered)
        {
            settings = remembered;
        }
        else
        {
            settings = JsonSerializer.Serialize(
                await _configuration.GetConfigurationAsync<Settings>(ct) ?? new Settings());

            if (_remembering is not null)
            {
                await _remembering();
            }

            // Remembered only if nothing was saved while it was being read.
            // Otherwise this is an answer the save has already replaced, and
            // keeping it would leave the plugin running on settings the owner
            // has changed away from until the next save. The swap fails when a
            // save has put a new one in since (_remembered says why it is one step).
            Interlocked.CompareExchange(ref _remembered, new Remembered(settings), seen);
        }

        // A new object every time, never the remembered one. The settings page
        // loads, applies what was typed and saves — and when a field is
        // refused, nothing is saved at all. Handing out one shared object would
        // leave the plugin running on values the owner was told were refused.
        return JsonSerializer.Deserialize<Settings>(settings) ?? new Settings();
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
            else if (!Cron.AtLeastFifteenMinutesApart(expression, out string? tooOften))
            {
                // The raw box under Show advanced saves through here as well,
                // so typing an expression by hand is no way round the floor.
                errors.Add($"The {name} cadence is refused: {tooOften}");
            }
        }

        // The JSON endpoint can carry any number; the page offers five names. None would silence the
        // plugin's errors as well as its chatter, and Critical is a level nothing here writes.
        if (!PluginLog.Offered.Contains(settings.LogLevel))
        {
            errors.Add($"The log level '{settings.LogLevel}' is not one of {string.Join(", ", PluginLog.Offered)}.");
        }

        // Asked once, and only used where a folder was refused: a save that
        // passes says nothing about storage at all.
        int before = errors.Count;

        CheckFolder("incomplete", settings.IncompleteFolder, errors);
        CheckFolder("intake", settings.IntakeFolder, errors);

        if (errors.Count > before && WhereItCanWrite() is { Length: > 0 } places)
        {
            for (int at = before; at < errors.Count; at++)
            {
                errors[at] += places;
            }
        }

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

        // Dropped rather than replaced with what was just written, so the next
        // load is what the host really stored. Only here: a save that was
        // refused above wrote nothing, and there is nothing to forget.
        Interlocked.Exchange(ref _remembered, new Remembered(null));

        // After the store has forgotten, so a listener that reads the settings
        // back reads what was just written. Handed what was saved as well, so a
        // listener that must act before the next line is written — the log
        // level — has nothing to wait for.
        Saved?.Invoke(settings);

        return new(true, errors, warnings);
    }

    /// <summary>Raised after a save the store accepted, and never after one it refused.</summary>
    /// <remarks>
    /// What a start owes — the torrents that were running, the clock — needs
    /// folders to owe it to, and on a fresh install they are set after the plugin
    /// has started. Until contract 12 the host's own tick brought the start round
    /// again; a plugin that is told nothing once it is loaded has to hear it here.
    /// </remarks>
    public event Action<Settings>? Saved;

    /// <summary>Raised with a secret's value as it is stored, before the store is even asked.</summary>
    /// <remarks>
    /// For the plugin's log, which blanks every secret it knows from every line. Raised first, so there is
    /// no moment in which the secret is stored and a line could still carry it.
    /// </remarks>
    public event Action<string>? SecretStored;

    /// <summary>Stores a secret. It never travels through <see cref="Settings"/>.</summary>
    public Task SetSecretAsync(string key, string value, CancellationToken ct)
    {
        SecretStored?.Invoke(value);

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

    /// <summary>Where the server says it can write, named as the owner sees them.</summary>
    private string WhereItCanWrite()
    {
        try
        {
            if (_places?.Invoke() is not IReadOnlyList<PluginStorageLocation> granted)
            {
                return string.Empty;
            }

            string[] places =
            [
                .. granted
                    .Where(one => one.Writable)
                    .Select(one => $"{one.Name} ({one.Kind})"),
            ];

            return places.Length == 0
                ? string.Empty
                : $" The server can write to: {string.Join(", ", places)}.";
        }
        catch (Exception)
        {
            // A refusal that cannot be enriched is still a refusal. Failing to
            // list the places must never turn a bad folder into a saved one, or
            // a good one into an error.
            return string.Empty;
        }
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

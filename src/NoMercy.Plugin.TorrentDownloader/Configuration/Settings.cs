using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Configuration;

/// <summary>
/// An indexer the owner added themselves.
/// </summary>
/// <remarks>
/// The API key is not here. This object is serialised whole into the host's
/// configuration file, in plaintext, so a key written into it would sit on disk
/// beside everything else — see <see cref="SettingsStore.IndexerApiKey"/> for
/// where it goes instead.
/// </remarks>
public sealed class OwnIndexer
{
    /// <summary>Stable, and what the secret store keys off. Never shown.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// With <c>{query}</c> where the search term goes, and <c>{apikey}</c>
    /// where the key does — the key itself is substituted at request time from
    /// the secret store, so the address stays showable on the page.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public int Priority { get; set; }

    public int MinimumIntervalSeconds { get; set; }

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// A private tracker the owner belongs to.
/// </summary>
/// <remarks>
/// The passkey is not here, for the same reason an API key is not on
/// <see cref="OwnIndexer"/>. <see cref="AnnounceTemplate"/> holds the announce
/// URL with <c>{passkey}</c> standing where the passkey goes, which is what
/// lets the page show the address without ever showing the secret in it.
/// </remarks>
public sealed class PrivateTracker
{
    public string Id { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public string AnnounceTemplate { get; set; } = string.Empty;
}

/// <summary>
/// Everything the owner can change. Defaults are in the types themselves and
/// come from docs/04-domain.md § Settings.
/// </summary>
public sealed class Settings
{
    /// <summary>Where downloads land. No default: only the owner knows.</summary>
    public string IncompleteFolder { get; set; } = string.Empty;

    /// <summary>Where finished video is staged for the encoder.</summary>
    public string IntakeFolder { get; set; } = string.Empty;

    public Cadences Cadences { get; set; } = new();

    public Profile Profile { get; set; } = new();

    public ClientLimits Client { get; set; } = new();

    public List<OwnIndexer> Indexers { get; set; } = [];

    public List<PrivateTracker> PrivateTrackers { get; set; } = [];

    /// <summary>Shipped sources the owner switched off, by name.</summary>
    public List<string> DisabledDefaultSources { get; set; } = [];

    /// <summary>Run the whole chain to a decision and hand nothing to the client.</summary>
    public bool DryRun { get; set; }
}

/// <summary>
/// What came of a save.
/// </summary>
/// <param name="Saved">
/// False when anything was refused, and then nothing was written at all: a
/// half-applied save leaves the plugin running settings the owner never agreed
/// to while the page shows the ones they typed.
/// </param>
/// <param name="Errors">Why it was refused, in words for the page.</param>
/// <param name="Warnings">Saved, but worth knowing.</param>
public sealed record SaveResult(
    bool Saved,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

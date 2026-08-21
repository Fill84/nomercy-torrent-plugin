namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>Whether the plugin's own client encrypts a peer connection.</summary>
public enum EncryptionPolicy
{
    /// <summary>Encrypt when the peer wants to, plain when it does not. The default.</summary>
    Allowed,

    /// <summary>Refuse a peer that will not encrypt.</summary>
    Required,

    Disabled,
}

/// <summary>
/// How the plugin's own torrent client behaves. Defaults from
/// docs/04-domain.md § Settings.
/// </summary>
public sealed class ClientLimits
{
    public int MaxConcurrentDownloads { get; set; } = 5;

    /// <summary>TCP and UDP, the same number for both.</summary>
    public int ListenPort { get; set; } = 51413;

    /// <summary>UPnP IGD, then NAT-PMP.</summary>
    public bool PortMapping { get; set; } = true;

    /// <summary>Bytes a second. Nought is unlimited.</summary>
    public long MaxDownloadRate { get; set; }

    public long MaxUploadRate { get; set; }

    public double SeedRatio { get; set; } = 1.0;

    /// <summary>Seeding stops at the ratio or these hours, whichever comes first.</summary>
    public int SeedHours { get; set; } = 48;

    /// <summary>No progress <em>and</em> no peers for this long is a stall.</summary>
    public int StallMinutes { get; set; } = 30;

    public int MetadataTimeoutMinutes { get; set; } = 5;

    /// <summary>
    /// How often resume data is written while a torrent is running.
    /// </summary>
    /// <remarks>
    /// A minute. docs/06-torrent-client.md names <c>ResumeInterval</c> and no
    /// document anywhere gives a number, so this is the one this client uses:
    /// it is what a crash costs in verification, and a minute of re-hashing is
    /// short enough not to matter and long enough that the disk is not busy
    /// writing resume files instead of the download.
    /// </remarks>
    public int ResumeIntervalSeconds { get; set; } = 60;

    public EncryptionPolicy Encryption { get; set; } = EncryptionPolicy.Allowed;

    /// <summary>
    /// Trackers attached to every grab, on top of the ones a source supplied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty to begin with and never typed in: the owner decided on
    /// 20 August 2026 that it is <em>learned</em>. Every tracker this plugin
    /// comes across — on a magnet, on a listing, on a torrent it is holding —
    /// is kept here without duplicates and travels with every grab afterwards.
    /// More trackers is a faster download, and the swarm one release was posted
    /// to is usually the swarm the next one is in.
    /// </para>
    /// <para>
    /// What may be kept is <see cref="TrackerBook"/>'s rule, and the one thing
    /// it never keeps is an address carrying a passkey: this list goes out with
    /// every grab, so the owner's own private tracker must never find its way
    /// into it.
    /// </para>
    /// </remarks>
    public List<string> DefaultTrackers { get; set; } = [];
}

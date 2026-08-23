namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// When to stop seeding.
/// </summary>
/// <param name="Ratio">Uploaded over downloaded, at which to stop. Nought is never.</param>
/// <param name="For">How long to seed at most. Zero is never.</param>
public sealed record SeedLimit(double Ratio, TimeSpan For)
{
    /// <summary>
    /// Whether this torrent is done seeding.
    /// </summary>
    /// <param name="priv">
    /// Whether the torrent is private. A public one is finished the moment it
    /// is complete: this client never uploads on a public swarm — see
    /// docs/06-torrent-client.md § Uploading — so staying in one gives nothing
    /// to anybody and costs the owner a connection and a slot.
    /// </param>
    /// <param name="ratio">What has been given back so far.</param>
    /// <param name="seeded">How long it has been seeding.</param>
    public bool Reached(bool priv, double ratio, TimeSpan seeded)
    {
        if (!priv)
        {
            return true;
        }

        // Whichever comes first, from docs/06-torrent-client.md. A limit of
        // nought is not a limit of nought seconds: it is nobody asking for one.
        return (Ratio > 0 && ratio >= Ratio) || (For > TimeSpan.Zero && seeded >= For);
    }
}

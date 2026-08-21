namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>
/// Every tracker this plugin has come across, kept and reused.
/// </summary>
/// <remarks>
/// <para>
/// The owner's decision, 20 August 2026: the default tracker list is not
/// something anybody types in. It is everything the plugin meets — on a magnet,
/// on a listing, on a torrent it is holding — with no duplicates, and it
/// travels with every grab afterwards. More trackers is a faster download, and
/// the swarm one release was posted to is usually the swarm the next one is in.
/// </para>
/// <para>
/// One thing is never kept, and it is not a preference. A private tracker's
/// announce address carries the owner's own passkey; this list goes out with
/// every grab, so learning one would hand their credentials to every public
/// swarm they download from. Anything shaped like a secret is refused.
/// </para>
/// </remarks>
public static class TrackerBook
{
    /// <summary>What a tracker can be reached over.</summary>
    /// <remarks>
    /// BEP 3 gives HTTP and BEP 15 gives UDP, and there is nothing else to
    /// announce to. A magnet's tracker field carries whatever was written into
    /// it, so what is not one of these is not a tracker.
    /// </remarks>
    private static readonly string[] Schemes = ["http", "https", "udp"];

    /// <summary>
    /// The list with everything newly come across added to it.
    /// </summary>
    /// <param name="known">What is already known, in the order it was learned.</param>
    /// <param name="seen">Whatever has just been met, in any state at all.</param>
    /// <param name="ownTrackers">
    /// The hosts of the owner's own private trackers. Theirs belong to the
    /// torrents those trackers issued and to nothing else.
    /// </param>
    public static IReadOnlyList<string> Learn(
        IEnumerable<string> known,
        IEnumerable<string> seen,
        IReadOnlyCollection<string> ownTrackers)
    {
        // First seen, first in the list, and what is already known keeps its
        // place. This is written into the owner's settings on every cycle, and
        // a list that reordered itself would rewrite the file for ever with
        // nothing having changed.
        List<string> kept = [];
        HashSet<string> already = new(StringComparer.OrdinalIgnoreCase);

        foreach (string tracker in known.Concat(seen))
        {
            string announce = tracker.Trim();

            if (announce.Length == 0 || !already.Add(announce) || !Worth(announce, ownTrackers))
            {
                continue;
            }

            kept.Add(announce);
        }

        return kept;
    }

    /// <summary>Whether this is something worth announcing to, and safe to keep.</summary>
    private static bool Worth(string announce, IReadOnlyCollection<string> ownTrackers)
    {
        if (!Uri.TryCreate(announce, UriKind.Absolute, out Uri? address)
            || !Schemes.Contains(address.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        // A query string is where a passkey lives, and no public tracker needs
        // one. Refusing the shape rather than looking for the word is what
        // makes this hold for a key nobody has thought of yet — and the same
        // goes for one hidden in the user information before the host.
        if (address.Query.Length > 0 || address.UserInfo.Length > 0)
        {
            return false;
        }

        return !ownTrackers.Contains(address.Host, StringComparer.OrdinalIgnoreCase);
    }
}

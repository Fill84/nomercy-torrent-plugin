namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// Where the client says what it decided and what went over the wire, for whoever is listening.
/// </summary>
/// <remarks>
/// <para>
/// An interface of this project's own rather than a logging library's, because this project references
/// nothing at all — its tests run against captured wire bytes, which stays possible only while it has no
/// idea a host exists (<c>ProjectIsolationTests</c>). The shell hands in one that writes to the plugin's
/// log.
/// </para>
/// <para>
/// Asked whether anybody is listening before a line is built, every time: the peer and piece code runs
/// many times a second, and a line nobody will read must cost a comparison and nothing more.
/// </para>
/// </remarks>
public interface IEngineLog
{
    /// <summary>Whether a Debug line would be written: the decision behind each step.</summary>
    bool Debugging { get; }

    /// <summary>Whether a Trace line would be written: wire detail.</summary>
    bool Tracing { get; }

    void Debug(string line);

    void Trace(string line);
}

/// <summary>The log nobody reads, and how a tracker is named in one.</summary>
public static class EngineLog
{
    /// <summary>For a client built with no log: every question answered no.</summary>
    public static IEngineLog Silent { get; } = new Nobody();

    /// <summary>A tracker as a line may name it: its scheme, host and port, and nothing else.</summary>
    /// <remarks>
    /// A private tracker's announce address carries the owner's passkey, as a parameter or as a path
    /// segment, and a torrent's own announce list brings that address here with no word of which part is
    /// secret. The host is what tells one tracker from another in a log, and it is never the secret.
    /// Built from the parts rather than cut from the front of the address: the front holds the user part,
    /// <c>http://user:secret@host</c>, which is where a tracker that takes a login keeps it.
    /// </remarks>
    public static string Host(string tracker)
    {
        return Uri.TryCreate(tracker, UriKind.Absolute, out Uri? address)
            ? $"{address.Scheme}://{address.Host}{(address.IsDefaultPort ? string.Empty : $":{address.Port}")}"
            : "a tracker whose address does not read as one";
    }

    private sealed class Nobody : IEngineLog
    {
        public bool Debugging => false;

        public bool Tracing => false;

        public void Debug(string line)
        {
        }

        public void Trace(string line)
        {
        }
    }
}

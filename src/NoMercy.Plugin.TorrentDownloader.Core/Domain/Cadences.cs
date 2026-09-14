namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>
/// How often a cycle is started when nobody starts one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This used to be four.</strong> Transfers every minute, feed every
/// fifteen, search every six hours and maintenance at four in the morning —
/// four schedules for four jobs that were never four jobs. They are the steps
/// of one cycle, and each one is started by the last one finishing: feed, then
/// search, then the downloads search asked for, then staging, then the encodes,
/// and maintenance only once there is nothing left in hand.
/// </para>
/// <para>
/// So there is one setting, and it answers one question: how often to start a
/// cycle if nothing else has. The other two ways one starts — the owner pressing
/// Run, and the server finishing a library scan — need no schedule at all.
/// </para>
/// <para>
/// Changing it takes effect at once. It is the plugin's own clock that keeps it,
/// not the host's job registry, which reads a plugin's schedule when the plugin
/// loads and never asks again: the owner changed a cadence on 3 September 2026,
/// watched the old one go on firing, and reasonably concluded the setting did
/// nothing.
/// </para>
/// </remarks>
public sealed class Cadences
{
    /// <summary>The name this cadence is kept under, and shown by.</summary>
    public const string Name = "cycle";

    /// <summary>Every hour, on the hour.</summary>
    public const string Hourly = "0 * * * *";

    public string Cycle { get; set; } = Hourly;

    /// <summary>The cadence with the name the owner sees for it.</summary>
    public IEnumerable<(string Name, string Expression)> All()
    {
        yield return (Name, Cycle);
    }
}

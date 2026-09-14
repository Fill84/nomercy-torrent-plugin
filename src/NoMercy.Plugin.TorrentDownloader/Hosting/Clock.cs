using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Storage;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// Decides which of the plugin's own cadences are due.
/// </summary>
/// <remarks>
/// <para>
/// The host reads <c>IScheduledTaskPlugin.Jobs</c> only when the plugin is
/// installed, hot-swapped or enabled — <c>PluginCronRegistrar.RegisterPlugin</c>
/// re-reads them, but only those three call it, and there is no way for a
/// plugin to ask for its own re-registration (media-server #53). So a saved
/// cadence cannot take effect by re-registering a job; it takes effect only
/// because this clock is asked fresh, on every tick, what is due now. See
/// docs/01-plugin.md § Cadences.
/// </para>
/// <para>
/// The four expressions are read fresh on every call rather than kept from
/// construction: a cadence saved while the server runs must be judged by its
/// new interval on the very next tick, and a clock that cached the old one
/// would be the exact fault this exists to remove.
/// </para>
/// </remarks>
public sealed class Clock(CadenceRepository cadences, TimeProvider time)
{
    /// <summary>Records that the cadence named <paramref name="name"/> finished, now.</summary>
    /// <remarks>
    /// After it finishes, never when it starts: recording a start would make a
    /// long run due again the moment it began, and the owner's next tick would
    /// start a second one on top of it.
    /// </remarks>
    public Task FinishedAsync(string name, CancellationToken ct)
    {
        return cadences.RecordFinishedAsync(name, time.GetUtcNow(), ct);
    }

    /// <summary>When the cadence named <paramref name="name"/> is next due, for the dashboard.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Null when the expression cannot be scheduled at all</strong>, and
    /// that is the safe answer rather than "now". The settings page refuses a
    /// cron on save; this is the second line of defence for a file edited by
    /// hand that never passed through it. Read as due, a bad expression would
    /// start a cycle every time anything looked, for ever.
    /// </para>
    /// <para>
    /// <strong>This replaced a DueAsync that asked "is anything due yet?".</strong>
    /// Nothing asks now: the plugin sets one timer for the moment this returns
    /// and is woken because a cycle is due rather than to find out whether one
    /// is.
    /// </para>
    /// </remarks>
    public async Task<DateTimeOffset?> NextAsync(string name, string expression, CancellationToken ct)
    {
        IReadOnlyDictionary<string, DateTimeOffset> finished = await cadences.LastFinishedAsync(ct);
        DateTimeOffset since = finished.TryGetValue(name, out DateTimeOffset when) ? when : DateTimeOffset.MinValue;

        return Cron.NextAfter(expression, since);
    }
}

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
    /// <summary>
    /// Which of <paramref name="expressions"/> are due at <paramref name="now"/>:
    /// one with no row at all — it has never run — or one whose next scheduled
    /// occurrence after it last finished has already arrived.
    /// </summary>
    /// <remarks>
    /// An expression <see cref="Cron.NextAfter"/> cannot parse answers null and
    /// is never due, never by default. The settings page already refuses one on
    /// save; this is the second line of defence for a file edited by hand that
    /// never passed through that page — treating null as due would run every
    /// cadence every minute from the moment somebody saved a bad cron, which is
    /// the opposite of what a refused expression should cost.
    /// </remarks>
    public async Task<IReadOnlyList<string>> DueAsync(
        IReadOnlyDictionary<string, string> expressions,
        DateTimeOffset now,
        CancellationToken ct)
    {
        IReadOnlyDictionary<string, DateTimeOffset> finished = await cadences.LastFinishedAsync(ct);

        List<string> due = [];

        foreach ((string name, string expression) in expressions)
        {
            DateTimeOffset since = finished.TryGetValue(name, out DateTimeOffset when) ? when : DateTimeOffset.MinValue;

            if (Cron.NextAfter(expression, since) is DateTimeOffset next && next <= now)
            {
                due.Add(name);
            }
        }

        return due;
    }

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
    /// <remarks>Null when its expression cannot be scheduled at all — see <see cref="DueAsync"/>.</remarks>
    public async Task<DateTimeOffset?> NextAsync(string name, string expression, CancellationToken ct)
    {
        IReadOnlyDictionary<string, DateTimeOffset> finished = await cadences.LastFinishedAsync(ct);
        DateTimeOffset since = finished.TryGetValue(name, out DateTimeOffset when) ? when : DateTimeOffset.MinValue;

        return Cron.NextAfter(expression, since);
    }
}

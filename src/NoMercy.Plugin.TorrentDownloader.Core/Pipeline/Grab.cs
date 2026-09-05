using System.Globalization;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>How much room there is where the downloads go.</summary>
/// <remarks>
/// A port because a folder on a server is not something Core may touch, and
/// because "how much is free" is the one number a test cannot arrange on a real
/// disk without filling it.
/// </remarks>
public interface IStorageSpace
{
    /// <summary>
    /// Bytes free where this folder is, or null when that cannot be told.
    /// </summary>
    /// <remarks>
    /// Null is not nought. A network share that will not say how much is free
    /// is not a share with no room, and refusing every grab on it would be a
    /// plugin that quietly stopped working.
    /// </remarks>
    long? FreeBytes(string folder);
}

/// <summary>What became of a grab.</summary>
public enum GrabResult
{
    /// <summary>The client took it.</summary>
    Taken,

    /// <summary>There is not enough room.</summary>
    NoRoom,

    /// <summary>The client would not have it, in its own words.</summary>
    Refused,
}

/// <summary>
/// A grab, and what happened to it.
/// </summary>
/// <param name="Result">Which of the three.</param>
/// <param name="InfoHash">What the client will know it by, when it took it.</param>
/// <param name="Reason">Why not, when it did not.</param>
/// <remarks>
/// This carried an <c>Attempt</c> saying whether a grab counted as a search
/// attempt against the episode — <strong>B2</strong>, which it never does. It
/// was hard-wired to false at all four construction sites and read by nothing,
/// a second expression of a rule that is really enforced elsewhere:
/// <c>EpisodeOutcome.Searched</c> gates it and <c>CycleRecord</c> counts against
/// <c>MaxSearchAttempts</c>. Two sources of truth for one rule is how they drift,
/// so there is one.
/// </remarks>
public sealed record Grabbed(GrabResult Result, string? InfoHash, string? Reason);

/// <summary>
/// Handing a chosen copy to the torrent client.
/// </summary>
/// <remarks>
/// Between deciding and downloading. It checks there is room, gathers every
/// tracker anybody named for the release, hands it over, and answers what
/// happened — and it never blames the episode for any of it.
/// </remarks>
public sealed class Grab(ITorrentEngine engine, IStorageSpace space, IActivityJournal journal)
{
    /// <summary>How many bytes this cycle has already promised the disk.</summary>
    /// <remarks>
    /// One Grab is made per cycle, so this is exactly the span over which the
    /// free space the disk reports has not yet caught up with what was taken.
    /// </remarks>
    private long _committed;

    /// <summary>
    /// Takes on one copy.
    /// </summary>
    /// <param name="copy">What the decide stage chose.</param>
    /// <param name="folder">Where downloads land.</param>
    /// <param name="defaultTrackers">The owner's own list, added to every grab.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<Grabbed> TakeAsync(
        ReleaseCopy copy,
        string folder,
        IReadOnlyList<string> defaultTrackers,
        CancellationToken ct)
    {
        if (Room(copy, folder) is string full)
        {
            journal.Failed(ActivityStage.Download, copy.Title, full);

            return new(GrabResult.NoRoom, null, full);
        }

        string? source = copy.Magnet ?? (copy.InfoHash is string hash ? $"magnet:?xt=urn:btih:{hash}" : null);

        if (source is null)
        {
            // Nothing to hand over. The find stage is supposed to have resolved
            // one, and a copy that reached here without is worth saying so
            // about rather than passing an empty string to the client.
            string nothing = $"{copy.Title} has no magnet and no info hash.";

            journal.Failed(ActivityStage.Download, copy.Title, nothing);

            return new(GrabResult.Refused, null, nothing);
        }

        try
        {
            TorrentHandle handle = await engine
                .AddAsync(
                    new(
                        source,

                        // Everything anybody named for it: what the site's
                        // magnet carried and the owner's own list. More
                        // trackers is a faster download and costs nothing.
                        [.. copy.Trackers.Union(defaultTrackers, StringComparer.OrdinalIgnoreCase)],
                        folder),
                    ct)
                .ConfigureAwait(false);

            journal.Finished(ActivityStage.Download, copy.Title, $"grabbed from {copy.Source}");

            // Counted against the disk from here, for as long as this cycle
            // lasts. A grab lives exactly one cycle, which is the span over
            // which the disk has not caught up with what was taken.
            _committed += copy.SizeBytes ?? 0;

            return new(GrabResult.Taken, handle.InfoHash, null);
        }
        catch (Exception refused) when (refused is not OperationCanceledException)
        {
            // In the client's own words. A grab that failed is the client's
            // fault or the network's, and never the episode's — so it does not
            // count as a search attempt.
            journal.Failed(ActivityStage.Download, copy.Title, refused.Message);

            return new(GrabResult.Refused, null, refused.Message);
        }
    }

    /// <summary>
    /// Why there is not room, or null when there is or nobody can say.
    /// </summary>
    /// <remarks>
    /// Checked before anything is handed over, because a torrent that fills the
    /// disk takes the media server down with it — the same disk holds the
    /// library and the database.
    /// </remarks>
    private string? Room(ReleaseCopy copy, string folder)
    {
        if (copy.SizeBytes is not long needed || space.FreeBytes(folder) is not long measured)
        {
            return null;
        }

        // Less what this cycle has already taken. Free space is what the disk
        // says now, and a torrent taken a moment ago has downloaded almost none
        // of itself yet — so ten grabs in one cycle were each measured against
        // the same number, every one of them passed, and together they filled
        // the disk. That is the one thing this check exists to stop.
        long free = measured - _committed;

        if (free >= needed)
        {
            return null;
        }

        // Both numbers, because "not enough space" tells the owner nothing they
        // can act on and these two say exactly what to clear.
        return $"{copy.Title} needs {Size(needed)} and {folder} has {Size(free)} free.";
    }

    /// <summary>
    /// Bytes as a person reads them.
    /// </summary>
    /// <remarks>
    /// Formatted in the invariant culture rather than the machine's. A server
    /// set to Dutch writes "3,7 GB", and a number whose meaning depends on
    /// where the machine is set up is one nobody can quote back reliably —
    /// this was caught by a test on a machine that is set to Dutch.
    /// </remarks>
    private static string Size(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes")
            : string.Create(CultureInfo.InvariantCulture, $"{size:0.#} {units[unit]}");
    }
}

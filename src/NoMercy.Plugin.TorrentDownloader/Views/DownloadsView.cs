using System.Globalization;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>One row of the Downloads page: what was grabbed and what the client says about it.</summary>
/// <param name="Grab">What this plugin grabbed.</param>
/// <param name="Transfer">What the client says, or null when it has not said anything yet.</param>
/// <param name="Destination">Where the bytes are landing.</param>
public sealed record DownloadRow(StoredDownload Grab, TorrentStatus? Transfer, string Destination);

/// <summary>
/// What is transferring, and what was grabbed and is not yet.
/// </summary>
/// <remarks>
/// <strong>G4.</strong> 0.3.4 rendered rows from the client's list alone, so a
/// grab the client had not taken up yet — or had quietly lost — was on no page
/// at all: the episode showed as unavailable, nothing was downloading, and
/// there was nowhere to find out why. The rows here come from the
/// <em>grabs</em>, and the transfer is what may be missing.
/// </remarks>
public static class DownloadsView
{
    public const string TableId = "downloads";

    public static PluginView Render(IReadOnlyList<DownloadRow> rows)
    {
        return new()
        {
            Layout = PluginLayout.ListDetail,
            Components =
            [
                PluginViews.Text("downloads-heading", "Downloads", "title"),
                Table(rows),
            ],
        };
    }

    private static PluginComponent Table(IReadOnlyList<DownloadRow> rows)
    {
        return PluginViews.Table(
            TableId,
            [
                new() { Key = "release", Label = "Release" },
                new() { Key = "state", Label = "State" },
                new() { Key = "progress", Label = "Progress" },
                new() { Key = "rate", Label = "Rate" },
                new() { Key = "peers", Label = "Peers" },
                new() { Key = "seeds", Label = "Seeds" },
                new() { Key = "ratio", Label = "Ratio" },
                new() { Key = "destination", Label = "Destination" },
            ],
            [
                .. rows.Select(row => PluginViews.Row(
                    $"{TableId}-{row.Grab.InfoHash}",
                    new Dictionary<string, object?>
                    {
                        ["release"] = row.Grab.ReleaseTitle,
                        ["state"] = State(row),
                        ["progress"] = Progress(row.Transfer),
                        ["rate"] = Rate(row.Transfer),

                        // A count nobody has been told is not nought. This is
                        // the whole of what 0.3.4 got wrong on this page.
                        ["peers"] = row.Transfer is null ? Unknown : row.Transfer.Peers,
                        ["seeds"] = row.Transfer is null ? Unknown : row.Transfer.Seeds,
                        ["ratio"] = row.Transfer?.Ratio is double ratio
                            ? ratio.ToString("0.00", CultureInfo.InvariantCulture)
                            : Unknown,
                        ["destination"] = row.Destination,
                    })),
            ],
            "Nothing has been grabbed.");
    }

    /// <summary>
    /// What the row says is happening.
    /// </summary>
    /// <remarks>
    /// A grab the client has not taken up says so in words rather than
    /// rendering as a torrent at nought per cent — one is waiting and the other
    /// is stuck, and an owner can act on the difference.
    /// </remarks>
    private static string State(DownloadRow row)
    {
        if (row.Transfer is null)
        {
            return $"grabbed, not started ({row.Grab.State.ToString().ToLowerInvariant()})";
        }

        return row.Transfer.Error is string wrong
            ? $"{row.Transfer.State.ToString().ToLowerInvariant()}: {wrong}"
            : row.Transfer.State.ToString().ToLowerInvariant();
    }

    private static string Progress(TorrentStatus? transfer)
    {
        if (transfer?.BytesTotal is not long total || total <= 0)
        {
            // A magnet with no metadata has no size, and a percentage of a size
            // nobody knows is a number made up on the page.
            return Unknown;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{transfer.BytesDone * 100.0 / total:0.#}% of {Bytes(total)}");
    }

    private static string Rate(TorrentStatus? transfer)
    {
        return transfer is null
            ? Unknown
            : $"{Bytes((long)transfer.DownloadRateBytesPerSecond)}/s ↓ {Bytes((long)transfer.UploadRateBytesPerSecond)}/s ↑";
    }

    /// <summary>What a number that is not known says instead of nought.</summary>
    private const string Unknown = "—";

    private static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        // Invariant, like every other number a person reads here: a figure
        // whose meaning depends on the server's locale cannot be quoted back.
        return string.Create(CultureInfo.InvariantCulture, $"{size:0.#} {units[unit]}");
    }
}

/// <summary>What happened, in the order it happened.</summary>
/// <param name="Event">grabbed, skipped, failed, dispatched or allowed.</param>
/// <param name="At">When.</param>
/// <param name="Subject">Which episode or release it was about.</param>
/// <param name="Detail">The reason, the library, or whatever that event carries.</param>
public sealed record HistoryLine(string Event, DateTimeOffset At, string Subject, string? Detail);

/// <summary>
/// The history page.
/// </summary>
/// <remarks>
/// Every kind of line carries its own reason, because "skipped" and "failed"
/// without one are the two entries an owner opens this page to understand.
/// </remarks>
public static class HistoryView
{
    public const string TableId = "history";

    public static PluginView Render(IReadOnlyList<HistoryLine> lines)
    {
        return new()
        {
            Layout = PluginLayout.ListDetail,
            Components =
            [
                PluginViews.Text("history-heading", "History", "title"),
                PluginViews.Table(
                    TableId,
                    [
                        new() { Key = "at", Label = "When" },
                        new() { Key = "event", Label = "What" },
                        new() { Key = "subject", Label = "Which" },
                        new() { Key = "detail", Label = "Why" },
                    ],
                    [
                        .. lines.Select((HistoryLine line, int index) => PluginViews.Row(
                            $"{TableId}-{index}",
                            new Dictionary<string, object?>
                            {
                                ["at"] = line.At.ToString("u", CultureInfo.InvariantCulture),
                                ["event"] = line.Event,
                                ["subject"] = line.Subject,

                                // Never blank: a line with no reason is the one
                                // an owner came here to read.
                                ["detail"] = line.Detail ?? "—",
                            })),
                    ],
                    "Nothing has happened yet."),
            ],
        };
    }
}

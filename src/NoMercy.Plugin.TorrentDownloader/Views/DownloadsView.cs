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

    // A control's "method" is the path the client posts to:
    // plugins/{id}/{method}, straight through. Naming the action instead of the
    // route gave every button on every page a URL this plugin does not serve,
    // and nothing anyone pressed did anything at all.

    /// <summary>Pausing a transfer, keeping its pieces.</summary>
    public static string PauseAction(string infoHash) => $"downloads/{infoHash}/pause";

    /// <summary>Starting a paused one again.</summary>
    public static string ResumeAction(string infoHash) => $"downloads/{infoHash}/resume";

    /// <summary>Stopping it, forgetting it, and putting its episodes back.</summary>
    public static string CancelAction(string infoHash) => $"downloads/{infoHash}/cancel";

    /// <summary>Taking on a torrent the owner found themselves.</summary>
    public const string AddAction = "downloads";

    public static PluginView Render(IReadOnlyList<DownloadRow> rows)
    {
        return new()
        {
            Layout = PluginLayout.ListDetail,
            Components =
            [
                Ui.Text("downloads-heading", "Downloads", "title"),
                Table(rows),

                // The controls sit under the table rather than in it: a table
                // cell holds a value and not a button, so a row can carry one
                // action and these are three. Each strip names its release, or
                // an owner with four downloads cannot tell which Cancel is
                // which.
                Ui.List("downloads-controls", [.. rows.Select(Controls)]),
                Add(),
            ],
        };
    }

    /// <summary>The three things an owner can do to one download.</summary>
    private static PluginComponent Controls(DownloadRow row)
    {
        bool paused = row.Transfer?.State == TorrentState.Paused;

        return Ui.Row(
            $"downloads-controls-{row.Grab.InfoHash}",
            Ui.Text($"downloads-controls-{row.Grab.InfoHash}-name", row.Grab.ReleaseTitle),
            Ui.Button(
                $"downloads-pause-{row.Grab.InfoHash}",
                paused ? "Resume" : "Pause",
                PluginActionIntent.CallPlugin(
                    // In the path, not the payload: that is where the route
                    // takes it, and a body beside it would be read by nothing.
                    paused ? ResumeAction(row.Grab.InfoHash) : PauseAction(row.Grab.InfoHash),
                    null,
                    PluginActionTransport.Rest)),
            Ui.Button(
                $"downloads-cancel-{row.Grab.InfoHash}",
                "Cancel",
                PluginActionIntent.CallPlugin(
                    CancelAction(row.Grab.InfoHash),
                    null,
                    PluginActionTransport.Rest,

                    // Confirmed, because it deletes what has been downloaded so
                    // far. The contract carries the confirmation so that every
                    // client asks the same question rather than each one
                    // deciding for itself.
                    new PluginConfirmation
                    {
                        Title = "Cancel this download?",
                        Message =
                            $"{row.Grab.ReleaseTitle} will be stopped, its part-downloaded files deleted, "
                            + "and its episodes looked for again.",
                        ConfirmLabel = "Cancel the download",
                        CancelLabel = "Leave it running",
                        Destructive = true,
                    })));
    }

    /// <summary>Adding a torrent by hand.</summary>
    /// <remarks>
    /// A magnet or a <c>.torrent</c>, which is what an owner has when they have
    /// found something the search chain did not. It runs through staging and
    /// the encode dispatch like any other download.
    /// </remarks>
    private static PluginComponent Add()
    {
        return Ui.Form(
            "downloads-add",
            "Add",
            PluginActionIntent.CallPlugin(AddAction, null, PluginActionTransport.Rest),
            new PluginFormField
            {
                Name = "source",
                Label = "Magnet or .torrent",
                Type = PluginFormFieldType.Text,
                Placeholder = "magnet:?xt=urn:btih:…",
            });
    }

    private static PluginComponent Table(IReadOnlyList<DownloadRow> rows)
    {
        return Ui.Table(
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
                .. rows.Select(row => Ui.Row(
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
                Ui.Text("history-heading", "History", "title"),
                Ui.Table(
                    TableId,
                    [
                        new() { Key = "at", Label = "When" },
                        new() { Key = "event", Label = "What" },
                        new() { Key = "subject", Label = "Which" },
                        new() { Key = "detail", Label = "Why" },
                    ],
                    [
                        .. lines.Select((HistoryLine line, int index) => Ui.Row(
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

using System.Globalization;
using System.Text;

using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>One row of the Downloads page: what was grabbed and what the client says about it.</summary>
/// <param name="Grab">What this plugin grabbed.</param>
/// <param name="Transfer">What the client says, or null when it has not said anything yet.</param>
public sealed record DownloadRow(StoredDownload Grab, TorrentStatus? Transfer)
{
    /// <summary>What the server last said went wrong with this grab's encode, or null.</summary>
    /// <remarks>
    /// Drawn beside the state rather than as it: the grab is still waiting — the
    /// server tries a failed encode again — so "encoding" stays true, and the
    /// dashboard, which counts downloads by their state, keeps counting it there.
    /// </remarks>
    public string? EncodeFailure { get; init; }
}

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

    public static PluginView Render(IReadOnlyList<DownloadRow> rows, TableQuery? query = null)
    {
        IReadOnlyList<PluginComponent> table = Table(rows, query);

        return new()
        {
            Layout = PluginLayout.Wide,
            Components =
            [
                Ui.Text("downloads-heading", "Downloads", "title"),
                table[0],

                // The field straight under the table and the whole texts after it (docs/specs/pages.md): a
                // page of long release names would otherwise push the field down by a line each.
                Add(),
                .. table.Skip(1),
            ],
        };
    }

    /// <summary>
    /// What an owner can do to one download, in the row itself.
    /// </summary>
    /// <remarks>
    /// A row used to carry one action and no more, so these were drawn as a
    /// second list under the table: every release twice, the two lists to be
    /// read against each other by eye, and the table squeezed off the screen by
    /// the length of the second one. The contract has an actions cell now.
    /// </remarks>
    private static IReadOnlyList<PluginTableAction> Controls(DownloadRow row)
    {
        bool paused = row.Transfer?.State == TorrentState.Paused;

        return
        [
            new()
            {
                Label = paused ? "Resume" : "Pause",
                Action = PluginActionIntent.CallPlugin(
                    // In the path, not the payload: that is where the route
                    // takes it, and a body beside it would be read by nothing.
                    paused ? ResumeAction(row.Grab.InfoHash) : PauseAction(row.Grab.InfoHash),
                    null,
                    PluginActionTransport.Rest),
            },
            new()
            {
                Label = "Cancel",
                Variant = "danger",
                Action = PluginActionIntent.CallPlugin(
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
                    }),
            },
        ];
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

    private static IReadOnlyList<PluginComponent> Table(IReadOnlyList<DownloadRow> rows, TableQuery? query)
    {
        return TableRows.Table(
            TableId,
            [
                new() { Key = "release", Label = "Release" },
                new() { Key = "state", Label = "State" },
                new() { Key = "progress", Label = "Progress" },
                new() { Key = "rate", Label = "Rate" },
                // Seeds first. A seed has the whole file and can finish this
                // download on its own; a peer has part of it and may have none
                // of the part this client still needs. The owner reads the
                // column that decides whether a download will finish, and it
                // is this one.
                new() { Key = "seeds", Label = "Seeds" },
                new() { Key = "peers", Label = "Peers" },
                new() { Key = "ratio", Label = "Ratio" },

                // docs/08-ui.md § 46 asks this table for a Duration, and how
                // long it has left is the only thing on the page one could
                // hold. It was worked out on every status and drawn nowhere, so
                // an owner watching a forty-five gigabyte pack had no figure
                // for how long they were waiting.
                // Text, like every other cell in this table, and not the
                // Duration cell docs/08-ui.md § 46 suggests. A Duration cell is
                // handed raw seconds and formatted by the client — which is
                // right in principle, and cannot say "not known": app-web's
                // formatDuration answers an empty string for anything that is
                // not a finite number. A blank cell where the row's other
                // unknowns read "—" is the one thing this page is shaped
                // against, so the page says it itself.
                new() { Key = "left", Label = "Left" },

                // No Destination: every download lands in the same incomplete folder, so a column naming it
                // said nothing about its row, and with it the table was wider than the page. The owner's
                // decision of 15 October 2026.
                new() { Key = "controls", Label = string.Empty, Cell = PluginTableCellType.Actions },
            ],
            rows,
            row => Ui.Row(
                $"{TableId}-{row.Grab.InfoHash}",
                new Dictionary<string, object?>
                {
                    ["release"] = row.Grab.ReleaseTitle,
                    ["state"] = row.EncodeFailure is string failure ? $"{State(row)} — {failure}" : State(row),
                    ["progress"] = Progress(row.Transfer),
                    ["rate"] = Rate(row.Transfer),

                    // A count nobody has been told is not nought. This is
                    // the whole of what 0.3.4 got wrong on this page.
                    ["seeds"] = Swarm(row.Transfer?.Seeds, row.Transfer?.SwarmSeeds),
                    // Leechers, counted as leechers. A tracker answers
                    // with seeders and leechers and they are two
                    // populations, never one taken off the other: a seed
                    // has all of it, a leecher has not, and this column is
                    // the second of those on both sides.
                    ["peers"] = Swarm(row.Transfer?.Leechers, row.Transfer?.SwarmPeers),
                    // Null where it cannot be known — no size, or nothing
                    // moving. A remaining time worked out from a rate of
                    // nought inflates to infinity, and an invented figure
                    // is worse than none.
                    ["left"] = row.Transfer?.Eta is TimeSpan left ? Left(left) : Unknown,
                    ["ratio"] = row.Transfer?.Ratio is double ratio
                        ? ratio.ToString("0.00", CultureInfo.InvariantCulture)
                        : Unknown,
                    ["controls"] = Controls(row),
                }),
            "Nothing has been grabbed.",
            query,

            // The figures, not the words drawn for them: "10% of 1 GB" is before "9% of 1 GB" as text and
            // "900 KB/s" after "2 MB/s". A figure the client has not told is null, and sorts after the rest.
            new Dictionary<string, Func<DownloadRow, IComparable?>>
            {
                ["progress"] = row => row.Transfer?.BytesTotal is long total && total > 0
                    ? (double)row.Transfer.BytesDone / total
                    : null,
                ["rate"] = row => row.Transfer?.DownloadRateBytesPerSecond,
                ["seeds"] = row => row.Transfer?.Seeds,
                ["peers"] = row => row.Transfer?.Leechers,
                ["left"] = row => row.Transfer?.Eta,
                ["ratio"] = row => row.Transfer?.Ratio,
            },

            // Nine columns at the 1,600 px of LongCells, each at its widest real content: Progress for a 4K
            // season pack "45.3% of 234.5 GB" 177 px (Bytes never draws four digits before a unit), Rate
            // "999.9 KB/s" 114, Seeds and Peers against a five-digit swarm "120 of 12345" 132 each (the
            // captures hold swarms of 2602), Ratio "12.34" and Left "59.5m" 69 each, the two buttons 150:
            // 843 px. State keeps every state the page writes itself whole, "grabbed, not started
            // (downloading)" at 34 characters, 330 px. The release has the 427 px left, 44 characters: a
            // row of 1,593 px.
            longColumns:
            [
                new("release", 44, Cut.AtWord),
                new("state", 34),
            ]);
    }

    /// <summary>
    /// What the row says is happening.
    /// </summary>
    /// <remarks>
    /// A grab the client has not taken up says so in words rather than
    /// rendering as a torrent at nought per cent — one is waiting and the other
    /// is stuck, and an owner can act on the difference.
    /// </remarks>
    internal static string State(DownloadRow row)
    {
        return row.Transfer?.Error is string wrong ? $"{StateWithoutReason(row)}: {wrong}" : StateWithoutReason(row);
    }

    /// <summary>The state in its own words, without the client's error.</summary>
    /// <remarks>
    /// What the Activity page counts downloads by. Counted with the error, every refused torrent was a group
    /// of its own as long as its error, in a cell that does not wrap; the error is on the Downloads page.
    /// </remarks>
    internal static string StateWithoutReason(DownloadRow row)
    {
        if (row.Transfer is null)
        {
            // A grab past the download is not one the client has not started.
            // It has finished and is waiting on the encoder, and reading
            // "grabbed, not started (staged)" against it says the opposite of
            // what is happening.
            return row.Grab.State switch
            {
                GrabState.Staged => "waiting for the encoder to take it",
                GrabState.Dispatched => "encoding",
                _ => $"grabbed, not started ({Words(row.Grab.State.ToString())})",
            };
        }

        return Words(row.Transfer.State.ToString());
    }

    /// <summary>
    /// How many are connected, and how many there are to connect to.
    /// </summary>
    /// <remarks>
    /// Nought connected out of three hundred seeds is a client that has not met
    /// anybody yet. Nought out of nought is a release nobody is serving. Drawn
    /// as one number those read the same, and the owner is looking at this
    /// column to decide whether a download is worth waiting for.
    ///
    /// The swarm's own count comes from the trackers, which report it on every
    /// announce; it was read for the interval and thrown away.
    /// </remarks>
    private static string Swarm(int? connected, int? swarm)
    {
        if (connected is not int has)
        {
            // A count nobody has been told is not nought. The whole of what
            // 0.3.4 got wrong on this page.
            return Unknown;
        }

        return swarm is int all
            ? $"{has} of {all}"
            : has.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A state's own name, as words an owner reads.</summary>
    /// <remarks>
    /// The name is lower-cased for the page, and lower-casing on its own ran
    /// the words together: <c>FetchingMetadata</c> reached the owner's screen
    /// as "fetchingmetadata", which is not a word, in the column read first to
    /// know what a download is doing.
    /// </remarks>
    private static string Words(string name)
    {
        StringBuilder said = new(name.Length + 4);

        foreach (char letter in name)
        {
            // Before each capital but the first, which is where one word of a
            // name in this vocabulary ends and the next begins.
            if (char.IsUpper(letter) && said.Length > 0)
            {
                said.Append(' ');
            }

            said.Append(char.ToLowerInvariant(letter));
        }

        return said.ToString();
    }

    private static string Progress(TorrentStatus? transfer)
    {
        if (transfer?.BytesTotal is not long total || total <= 0)
        {
            // A magnet with no metadata has no size, and a percentage of a size
            // nobody knows is a number made up on the page.
            return Unknown;
        }

        // The percentage of the size and nothing beside it: the owner's decision of 15 October 2026. What
        // has arrived and is not verified yet, "(8.3 MB in)", made the column too wide for the page.
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{transfer.BytesDone * 100.0 / total:0.#}% of {Bytes(total)}");
    }

    /// <remarks>
    /// The download rate only, the owner's decision of 15 October 2026: nothing on a public swarm is ever
    /// uploaded (docs/06-torrent-client.md), so the rate going out read nought on almost every row.
    /// </remarks>
    private static string Rate(TorrentStatus? transfer)
    {
        return transfer is null ? Unknown : $"{Bytes((long)transfer.DownloadRateBytesPerSecond)}/s";
    }

    /// <summary>What a number that is not known says instead of nought.</summary>
    private const string Unknown = "—";

    /// <summary>How long is left, in the largest unit that says something.</summary>
    /// <remarks>
    /// Days for a starved pack, minutes for an ordinary episode. "4320m" and
    /// "3d" are the same number and only one of them is read at a glance.
    /// </remarks>
    private static string Left(TimeSpan left)
    {
        return left.TotalDays >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{left.TotalDays:0.#}d")
            : left.TotalHours >= 1
                ? string.Create(CultureInfo.InvariantCulture, $"{left.TotalHours:0.#}h")
                : string.Create(CultureInfo.InvariantCulture, $"{left.TotalMinutes:0.#}m");
    }

    private static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;

        // Counted in 1024s as before, but the next unit from 1000 of this one, so no figure has four digits
        // before its unit: "1005.9 KB/s" and "1020.4 MB" made the Rate and Progress columns wider than the
        // page was measured at (docs/08-ui.md § Long text). 1000 is what it rounds to that counts, so 999.97
        // is not drawn as "1000".
        while (Math.Round(size, 1) >= 1000 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        // Below one of the new unit, two decimals: "0.98 MB" rather than a "1 MB" that is 2% wrong. Never
        // longer than "999.9 KB", which is what the columns are measured at.
        string format = unit > 0 && size < 1 ? "0.##" : "0.#";

        // Invariant, like every other number a person reads here: a figure
        // whose meaning depends on the server's locale cannot be quoted back.
        return size.ToString(format, CultureInfo.InvariantCulture) + " " + units[unit];
    }
}

/// <summary>What happened, in the order it happened.</summary>
/// <param name="Event">grabbed, decided, skipped, failed, lost, dispatched or allowed.</param>
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

    public static PluginView Render(IReadOnlyList<HistoryLine> lines, TableQuery? query = null)
    {
        return new()
        {
            Layout = PluginLayout.Wide,
            Components =
            [
                Ui.Text("history-heading", "History", "title"),
                .. TableRows.Table(
                    TableId,
                    [
                        new() { Key = "at", Label = "When" },
                        new() { Key = "event", Label = "What" },
                        new() { Key = "subject", Label = "Which" },
                        new() { Key = "detail", Label = "Why" },
                    ],
                    lines.Select((HistoryLine line, int index) => (Line: line, Index: index)),
                    entry => Ui.Row(
                        $"{TableId}-{entry.Index}",
                        new Dictionary<string, object?>
                        {
                            ["at"] = entry.Line.At.ToString("u", CultureInfo.InvariantCulture),
                            ["event"] = entry.Line.Event,
                            ["subject"] = entry.Line.Subject,

                            // Never blank: a line with no reason is the one
                            // an owner came here to read.
                            ["detail"] = entry.Line.Detail ?? "—",
                        }),
                    "Nothing has happened yet.",
                    query,
                    new Dictionary<string, Func<(HistoryLine Line, int Index), IComparable?>>
                    {
                        ["at"] = entry => entry.Line.At,

                        // A line with no reason says "—", which is not a reason that sorts among the others.
                        ["detail"] = entry => entry.Line.Detail,
                    },

                    // A reason quotes the client or the server, whose words are as long as they are.
                    longColumns:
                    [
                        // A line about a release rather than an episode names the release, up to 81
                        // characters in the captures. When and What take 318 px of the 1,600 (LongCells),
                        // and the reason 717 at its 77 characters: 60 are left.
                        new("subject", 60, Cut.AtWord),
                        new("detail"),
                    ]),
            ],
        };
    }
}

using System.Globalization;

using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// The settings page at <c>/settings</c>.
/// </summary>
/// <remarks>
/// Handed the names of the secrets that exist and never their values, so it can
/// say whether a passkey is set and has no way at all to say what it is. That
/// is a property of the signature rather than of the care taken while writing
/// the body.
/// </remarks>
public static class SettingsView
{
    public const string FormId = "settings";

    // A control's "method" is the path the client posts to:
    // plugins/{id}/{method}, straight through.

    /// <summary>Starting a cycle now.</summary>
    public const string RunAction = "run";

    /// <summary>Cancelling the running one.</summary>
    public const string StopAction = "stop";

    /// <summary>Saving whatever section of the page was filled in.</summary>
    public const string SaveAction = "settings/edit";

    /// <summary>Shows or hides every advanced block on this page.</summary>
    /// <remarks>
    /// An action rather than a field, because a field would be posted and
    /// written down. <strong>Show advanced is a display state:</strong> it
    /// changes what is drawn and nothing about what the plugin does, and a
    /// field hidden behind it still applies.
    /// </remarks>
    public const string AdvancedAction = "settings/advanced";

    /// <summary>
    /// The page. <c>secretsSet</c> is the keys the secret store holds — names
    /// only, see the remarks above — and <c>problems</c> is why the last save
    /// was refused, if it was.
    /// </summary>
    public static PluginView Render(
        Settings settings,
        IReadOnlyCollection<string> secretsSet,
        IReadOnlyList<string> problems,
        PortState port = PortState.Unknown,
        bool advanced = false)
    {
        HashSet<string> present = new(secretsSet, StringComparer.Ordinal);

        // Seeding decides nothing without one. docs/06-torrent-client.md
        // § Uploading: a public torrent never uploads, not while it is
        // downloading and not once it is finished.
        bool privately = settings.PrivateTrackers.Count > 0;

        List<PluginFormField> fields =
        [
            .. Folders(settings),

            // No quality, codec, tag or language here any more: those are set per show and per
            // library on the overview (docs/specs/show-list.md).
            .. Cadences(settings.Cadences),
            .. Client(settings.Client),
        ];

        if (privately)
        {
            fields.AddRange(Seeding(settings.Client));
        }

        if (advanced)
        {
            fields.AddRange(Advanced(settings));
        }

        List<PluginComponent> page =
        [
            .. problems.Select((string problem, int index) =>
                Ui.Text($"problem-{index}", problem, "caption")),
            AdvancedToggle(advanced),

            // One form and one Save, for everything on the page. The owner
            // asked for it on 12 September 2026 after seeing four of them
            // scattered down the page, which reverses the decision of the same
            // week that gave each group its own.
            //
            // It costs what that decision was avoiding: a form posts the fields
            // it holds, so one Save is one post, and a single refused field
            // saves none of them. The refusal names the field, which is what
            // makes that bearable - and it is drawn at the top of this page, in
            // problems, rather than left in a log.
            Ui.Form(
                FormId,
                "Save",
                PluginActionIntent.CallPlugin(SaveAction, null, PluginActionTransport.Rest),
                [.. fields]),
            Port(settings, port),
            TrackerList(settings, present, privately),
            Running(),
            Answering(),
        ];

        return new()
        {
            Layout = PluginLayout.Wide,
            Components = [.. page],
        };
    }

    /// <summary>
    /// The one switch, and it saves nothing.
    /// </summary>
    /// <remarks>
    /// In a row, and every other button on this page is too. A
    /// <c>PluginButton</c> is <c>inline-flex</c> and asks to be as wide as its
    /// words, but a page column and a <c>PluginDetail</c> body are both
    /// <c>flex-col</c>, and a flex column stretches its children across. This
    /// drew as a full-width strip with the words at the far left, which reads
    /// as a heading rather than something to press. A <c>PluginRow</c> is
    /// <c>flex-row items-center</c> and stretches nothing.
    /// </remarks>
    private static PluginComponent AdvancedToggle(bool advanced)
    {
        return Ui.Row(
            "advanced-row",
            Ui.Button(
                "advanced-toggle",
                advanced ? "Hide advanced" : "Show advanced",
                PluginActionIntent.CallPlugin(AdvancedAction, null, PluginActionTransport.Rest)));
    }

    /// <summary>What is known about the listening port, and a warning only when it is shut.</summary>
    /// <remarks>
    /// <para>
    /// Beside the port the section above it edits, because that is the number
    /// this is about. Three states and one warning: <em>open</em> and <em>not
    /// known yet</em> are states rather than problems, and a page that decorates
    /// both of them with a warning has taught the owner to ignore warnings.
    /// </para>
    /// <para>
    /// <strong>No mapping result reaches this method, and that is the point.</strong>
    /// It used to be handed one and drew "the router would not open port 51413 —
    /// forward TCP and UDP 51413 by hand" from it, on a machine where 51413 had
    /// been forwarded by hand for months. UPnP and NAT-PMP failing says the
    /// router would not open the port <em>itself</em>; it says nothing about
    /// whether the port is open. The router's answer is logged and never drawn.
    /// </para>
    /// </remarks>
    private static PluginComponent Port(Settings settings, PortState port)
    {
        (string Says, string Variant) said = port switch
        {
            PortState.Open => ("open", PluginBadgeVariant.Success),
            PortState.Shut => ("shut", PluginBadgeVariant.Warning),
            _ => ("not known yet", PluginBadgeVariant.Neutral),
        };

        return Ui.Row(
            "port",
            Ui.Text(
                "port-number",
                $"Listening port {settings.Client.ListenPort.ToString(CultureInfo.InvariantCulture)}",
                "caption"),
            Ui.Badge("port-state", said.Says, said.Variant));
    }

    /// <remarks>
    /// Nothing downloads until both of these are set, so this is the first
    /// section on the page and it is the one that must be fillable.
    /// </remarks>
    private static PluginFormField[] Folders(Settings settings)
    {
        return
        [
            new PluginFormField
            {
                Name = "incompleteFolder",
                Label = "Incomplete folder — where a download lands while it runs",

                // Chosen rather than typed. This was a text box with an example
                // path beside it, so the owner typed the path by hand and a typo
                // was a plugin with nowhere to download to and nothing saying
                // why. The field is media-server #33, which this plugin asked
                // for and then went on not using.
                Type = PluginFormFieldType.Folder,
                Value = settings.IncompleteFolder,
                Placeholder = @"D:\torrents\incomplete",
            },
            new PluginFormField
            {
                Name = "intakeFolder",
                Label = "Intake folder — where finished video is staged for the encoder",
                Type = PluginFormFieldType.Folder,
                Value = settings.IntakeFolder,
                Placeholder = @"D:\torrents\intake",
            }];
    }

    /// <summary>
    /// How often a cycle is started when nobody starts one, chosen from a list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One, where there were four.</strong> Transfers, feed, search and
    /// maintenance are the steps of one cycle, each started by the last one
    /// finishing, so the only schedule left is when to start one.
    /// </para>
    /// <para>
    /// <strong>Cron is not a language the owner has to know.</strong> The ordinary
    /// control is an interval whose values are the expressions they stand for,
    /// so nothing in between has to translate. Whoever wants to type one still
    /// can, under <strong>Show advanced</strong> — and it is held to the same
    /// floor of fifteen minutes.
    /// </para>
    /// <para>
    /// <strong>And no label says "takes effect on the next server restart" any
    /// more.</strong> They all did, because the host reads
    /// <c>IScheduledTaskPlugin.Jobs</c> only when a plugin is installed or
    /// enabled. <c>S12-05</c> gave the plugin its own clock for that reason, so
    /// a saved cadence is due by its new interval without a restart and the
    /// sentence became untrue.
    /// </para>
    /// </remarks>
    private static PluginFormField[] Cadences(Cadences cadences)
    {
        return
        [
            Every("cadences.cycle", "Start a cycle", cadences.Cycle)];
    }

    /// <summary>The intervals offered, and what each one really is.</summary>
    /// <remarks>
    /// <strong>Every 15 minutes to once a day</strong>, the list of <c>docs/specs/pages.md</c>
    /// § Settings. Fifteen minutes is the floor the save holds a typed expression to as well — see
    /// <c>Cron.AtLeastFifteenMinutesApart</c> — and for anything sooner there is the Run button.
    /// </remarks>
    private static readonly (string Says, string Cron)[] Intervals =
    [
        ("every 15 minutes", "*/15 * * * *"),
        ("every 30 minutes", "*/30 * * * *"),
        ("every hour", "0 * * * *"),
        ("every 6 hours", "0 */6 * * *"),
        ("every 12 hours", "0 */12 * * *"),
        ("once a day, at 4am", "0 4 * * *"),
    ];

    /// <summary>
    /// One cadence, as an interval.
    /// </summary>
    /// <remarks>
    /// <strong>A stored expression the list does not offer is added to it.</strong>
    /// Without that the list has nothing to select and draws empty, which says
    /// the cadence is unset while it is running perfectly well — and saving
    /// from there writes whatever the empty box falls back to. Seen on the
    /// owner's own server on 12 September 2026, the first time this page was
    /// looked at with real settings behind it: Transfers was on an expression
    /// none of these offers and the box read "Select...".
    /// </remarks>
    private static PluginFormField Every(string name, string label, string cron)
    {
        bool offered = Intervals.Any(one => string.Equals(one.Cron, cron, StringComparison.Ordinal));

        return new()
        {
            Name = name,
            Label = label,
            Type = PluginFormFieldType.Select,
            Value = cron,
            Options =
            [
                .. Intervals.Select(one => new PluginFormOption { Label = one.Says, Value = one.Cron }),
                .. offered || cron.Length == 0
                    ? Array.Empty<PluginFormOption>()
                    : [new PluginFormOption { Label = cron, Value = cron }],
            ],
        };
    }

    /// <summary>
    /// The client: how much at once, how fast down, and the port.
    /// </summary>
    /// <remarks>
    /// Seeding is not here. Seed ratio, seed hours and the upload limit apply
    /// to private torrents alone - docs/06-torrent-client.md section Uploading -
    /// so they live with the private trackers and are drawn only when one
    /// exists. The expert fields went to the advanced block.
    /// </remarks>
    private static PluginFormField[] Client(ClientLimits limits)
    {
        return
        [
            new PluginFormField
            {
                Name = "client.maxConcurrentDownloads",
                Label = "Downloads at once",
                Type = PluginFormFieldType.Number,
                Value = limits.MaxConcurrentDownloads,
            },
            Speed("client.maxDownloadRate", "Maximum download", limits.MaxDownloadRate),
            Typed("client.maxDownloadRateMb", "or type it, in MB/s"),
            new PluginFormField
            {
                Name = "client.listenPort",
                Label = "Listen port (TCP and UDP)",
                Type = PluginFormFieldType.Number,
                Value = limits.ListenPort,
            }];
    }

    /// <summary>Seeding, which only a private tracker makes mean anything.</summary>
    private static PluginFormField[] Seeding(ClientLimits limits)
    {
        return
        [
            new PluginFormField
            {
                Name = "client.seedRatio",
                Label = "Seed until this ratio",
                Value = limits.SeedRatio.ToString(CultureInfo.InvariantCulture),
            },
            new PluginFormField
            {
                Name = "client.seedHours",
                Label = "or this many hours, whichever comes first",
                Type = PluginFormFieldType.Number,
                Value = limits.SeedHours,
            },
            Speed("client.maxUploadRate", "Maximum upload", limits.MaxUploadRate),
            Typed("client.maxUploadRateMb", "or type it, in MB/s")];
    }

    /// <summary>
    /// A speed, as the answers people actually give.
    /// </summary>
    /// <remarks>
    /// Stored in bytes a second, which is what the client reads and what these
    /// values are, so nothing downstream changes. The page simply stops asking
    /// the owner to type 10485760.
    /// </remarks>
    private static PluginFormField Speed(string name, string label, long bytes)
    {
        return new()
        {
            Name = name,
            Label = label,
            Type = PluginFormFieldType.Select,
            Value = bytes.ToString(CultureInfo.InvariantCulture),
            Options =
            [
                new PluginFormOption { Label = "unlimited", Value = "0" },
                new PluginFormOption { Label = "1 MB/s", Value = "1048576" },
                new PluginFormOption { Label = "5 MB/s", Value = "5242880" },
                new PluginFormOption { Label = "10 MB/s", Value = "10485760" },
                new PluginFormOption { Label = "25 MB/s", Value = "26214400" },
            ],
        };
    }

    /// <summary>
    /// The box beside a preset list, for a speed the list does not offer.
    /// </summary>
    /// <remarks>
    /// Drawn empty every time, and blank means leave the answer of the list
    /// alone. Showing the stored speed here would make the box and the list two
    /// controls claiming the same number, and saving would then turn every
    /// preset into whatever the box happened to be showing.
    /// </remarks>
    private static PluginFormField Typed(string name, string label)
    {
        return new()
        {
            Name = name,
            Label = label,
            Type = PluginFormFieldType.Number,
            Value = string.Empty,
            Placeholder = "leave empty to use the list",
        };
    }

    /// <summary>Everything an owner should not have to walk past to change a folder.</summary>
    private static PluginFormField[] Advanced(Settings settings)
    {
        return
        [
            new PluginFormField
            {
                Name = "client.stallMinutes",
                Label = "Minutes with no progress and no peers before it counts as stalled",
                Type = PluginFormFieldType.Number,
                Value = settings.Client.StallMinutes,
            },
            new PluginFormField
            {
                Name = "client.metadataTimeoutMinutes",
                Label = "Minutes to wait for the metadata of a magnet",
                Type = PluginFormFieldType.Number,
                Value = settings.Client.MetadataTimeoutMinutes,
            },
            new PluginFormField
            {
                Name = "client.encryption",
                Label = "Encryption",
                Type = PluginFormFieldType.Select,
                Value = settings.Client.Encryption.ToString(),
                Options =
                [
                    .. Enum.GetNames<EncryptionPolicy>()
                        .Select(name => new PluginFormOption { Label = name, Value = name }),
                ],
            },
            new PluginFormField
            {
                Name = "client.resumeIntervalSeconds",
                Label = "Seconds between writing what a download has got so far",
                Type = PluginFormFieldType.Number,
                Value = settings.Client.ResumeIntervalSeconds,
            },
            Cron("cadences.cycle", "Starting a cycle, as an expression", settings.Cadences.Cycle)];
    }

    /// <summary>One cadence, typed out, for whoever wants it.</summary>
    private static PluginFormField Cron(string name, string label, string cron)
    {
        return new() { Name = name, Label = label, Value = cron };
    }

    /// <summary>
    /// The trackers of the owner, and why seeding is missing without one.
    /// </summary>
    /// <remarks>
    /// The absence is explained rather than left to be noticed. Three controls
    /// that cannot affect anything are three an owner reasonably expects to
    /// work, and a page that simply omits them invites the question of where
    /// they went.
    /// </remarks>
    private static PluginComponent TrackerList(Settings settings, HashSet<string> present, bool privately)
    {
        return Ui.Detail(
            "tracker-list",
            "Private trackers",
            privately
                ? null
                : "None added. Seed ratio, seed hours and the upload limit are not shown: nothing on a "
                  + "public swarm is ever uploaded, so they would decide nothing.",
            null,
            [
                .. settings.PrivateTrackers.SelectMany(tracker => (PluginComponent[])
                [
                    Ui.Text($"tracker-{tracker.Id}", $"{tracker.Host} - {Or(tracker.AnnounceTemplate, "no announce URL")}"),
                    Secret($"tracker-{tracker.Id}-passkey", "Passkey", present.Contains(SettingsStore.TrackerPasskey(tracker.Id))),
                ]),
            ]);
    }

    /// <summary>Starting a cycle, and stopping one.</summary>
    /// <remarks>
    /// These were text until 21 August 2026, saying they did nothing because at
    /// the time nothing was behind them. Sprint 8 built the pipeline and no
    /// slice came back to turn them into controls, so the plugin had no way at
    /// all to be asked to do something.
    /// </remarks>
    private static PluginComponent Running()
    {
        return Ui.Detail(
            "run",
            "Run",
            "A cycle looks for every missing episode and downloads what it settles on.",
            null,
            // Side by side in a row rather than as two items of this panel:
            // the panel body is a flex column and stretched each of them into
            // a full-width bar.
            Ui.Row(
                "run-buttons",
                Ui.Button(
                    "run-run",
                    "Run now",
                    PluginActionIntent.CallPlugin(RunAction, null, PluginActionTransport.Rest),
                    variant: "primary"),
                Ui.Button(
                    "run-stop",
                    "Stop",
                    PluginActionIntent.CallPlugin(StopAction, null, PluginActionTransport.Rest))));
    }

    /// <summary>Which copy of the plugin drew this page.</summary>
    /// <remarks>
    /// <para>
    /// At the foot of the page, and asked for on 18 September 2026: the plugin updates from the catalogue
    /// without a restart, and nothing anywhere in it said which copy was answering. Whether a fix is in could
    /// only be told from what the plugin did next.
    /// </para>
    /// <para>
    /// The running assembly's own identity, never the manifest on disk and never the catalogue. Those two say
    /// what is installed, and what is installed and what is answering are exactly the pair that come apart
    /// after an update — which is the whole reason this is here.
    /// </para>
    /// </remarks>
    private static PluginComponent Answering()
    {
        return Ui.Text(
            "version",
            $"{PluginIdentity.Name} {PluginIdentity.Version.ToString(3)}",
            "caption");
    }

    /// <summary>Whether a secret is stored — never which one, and never what.</summary>
    private static PluginComponent Secret(string id, string label, bool isSet)
    {
        return Ui.Text(id, $"{label}: {(isSet ? "set" : "not set")}", "caption");
    }

    private static string Rate(string what, long bytesPerSecond)
    {
        return bytesPerSecond == 0 ? $"{what}: unlimited" : $"{what}: {bytesPerSecond} bytes/s";
    }

    private static string Or(string value, string whenMissing)
    {
        return string.IsNullOrWhiteSpace(value) ? whenMissing : value;
    }
}

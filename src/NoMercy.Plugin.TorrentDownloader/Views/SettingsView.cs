using System.Globalization;

using NoMercy.Plugin.TorrentDownloader.Bittorrent;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugins.Abstractions;

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

    /// <summary>
    /// The page. <c>secretsSet</c> is the keys the secret store holds — names
    /// only, see the remarks above — and <c>problems</c> is why the last save
    /// was refused, if it was.
    /// </summary>
    public static PluginView Render(
        Settings settings,
        IReadOnlyCollection<string> secretsSet,
        IReadOnlyList<string> problems,
        PortMapResult? mapping = null)
    {
        HashSet<string> present = new(secretsSet, StringComparer.Ordinal);

        return new()
        {
            Layout = PluginLayout.Standard,
            Components =
            [
                .. problems.Select((string problem, int index) =>
                    Ui.Text($"problem-{index}", problem, "caption")),
                .. Port(settings, mapping),
                Folders(settings),
                CadenceSection(settings.Cadences),
                Quality(settings.Profile),
                Client(settings.Client, settings.DryRun),
                Indexers(settings, present),
                Trackers(settings, present),
                Running(settings),
            ],
        };
    }

    /// <summary>
    /// What became of the attempt to have the router open the listening port.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing at all while it worked, and while it has not been tried. When
    /// both UPnP and NAT-PMP have refused, it says so and says what to do about
    /// it: the owner has to forward the port by hand, and a client nobody can
    /// dial is one that downloads from the few peers it reaches out to and
    /// seeds to nobody.
    /// </para>
    /// <para>
    /// With the router's own words underneath, because "port mapping failed"
    /// and "your router has UPnP turned off" are different problems and only
    /// one of them is worth walking to the cupboard for.
    /// </para>
    /// </remarks>
    private static IEnumerable<PluginComponent> Port(Settings settings, PortMapResult? mapping)
    {
        if (mapping is null || mapping.Mapped)
        {
            yield break;
        }

        yield return Ui.Text(
            "port-mapping",
            $"The router would not open port {settings.Client.ListenPort}. "
            + $"Forward TCP and UDP {settings.Client.ListenPort} to this machine by hand, "
            + "or no peer will be able to reach it.",
            "caption");

        if (mapping.Reason is string refused)
        {
            yield return Ui.Text("port-mapping-reason", refused, "caption");
        }
    }

    /// <remarks>
    /// Nothing downloads until both of these are set, so this is the first
    /// section on the page and it is the one that must be fillable.
    /// </remarks>
    private static PluginComponent Folders(Settings settings)
    {
        return Section(
            "folders",
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
            });
    }

    private static PluginComponent CadenceSection(Cadences cadences)
    {
        return Section(
            "cadences",
            new PluginFormField
            {
                Name = "cadences.transfers",
                // Said on every one of them, because the alternative is that
                // the owner changes a cron, watches the old one keep firing,
                // and concludes the setting does not work. The server registers
                // cadences once, when the plugin loads.
                Label = "Transfers — takes effect on the next server restart",
                Value = cadences.Transfers,
            },
            new PluginFormField
            {
                Name = "cadences.feed",
                Label = "Feed — takes effect on the next server restart",
                Value = cadences.Feed,
            },
            new PluginFormField
            {
                Name = "cadences.search",
                Label = "Search — takes effect on the next server restart",
                Value = cadences.Search,
            },
            new PluginFormField
            {
                Name = "cadences.maintenance",
                Label = "Maintenance — takes effect on the next server restart",
                Value = cadences.Maintenance,
            });
    }

    private static PluginComponent Quality(Profile profile)
    {
        return Section(
            "quality",
            new PluginFormField
            {
                Name = "profile.maximumResolution",

                // Not "maximum". The rule is one rung and never a ceiling -
                // 1080p means 1080p, and a 720p copy is refused rather than
                // taken as good enough. The label said the opposite of what
                // the code does, which is the page telling the owner something
                // untrue about their own library.
                Label = "Resolution",
                Type = PluginFormFieldType.Select,
                Value = profile.MaximumResolution,
                Options = [.. Profile.Resolutions.Select(Choice)],
            },
            new PluginFormField
            {
                Name = "profile.codec",
                Label = "Codec",

                // A list rather than a box. Typed by hand the field takes
                // anything, and anything is what it got: a codec spelled a way
                // the parser does not know refuses every release there is, and
                // an empty one is not "any" either.
                Type = PluginFormFieldType.Select,
                Value = profile.Codec,
                Options = [.. Profile.Codecs.Select(Choice)],
            },
            new PluginFormField
            {
                Name = "profile.requireCodecTag",
                Label = "Refuse a release that does not say which codec it is",
                Type = PluginFormFieldType.Toggle,
                Value = profile.RequireCodecTag,
            },
            new PluginFormField
            {
                Name = "profile.minimumSeeders",
                Label = "Minimum seeders",
                Type = PluginFormFieldType.Number,
                Value = profile.MinimumSeeders,
            },
            new PluginFormField
            {
                Name = "profile.allowSeasonPacks",
                Label = "Take season packs",
                Type = PluginFormFieldType.Toggle,
                Value = profile.AllowSeasonPacks,
            },
            new PluginFormField
            {
                Name = "profile.seasonPackThreshold",
                Label = "Gaps before a season pack is worth it",
                Type = PluginFormFieldType.Number,
                Value = profile.SeasonPackThreshold,
            },
            new PluginFormField
            {
                Name = "profile.englishOnly",
                Label = "English only",
                Type = PluginFormFieldType.Toggle,
                Value = profile.EnglishOnly,
            },
            new PluginFormField
            {
                Name = "profile.includeSpecials",
                Label = "Include specials",
                Type = PluginFormFieldType.Toggle,
                Value = profile.IncludeSpecials,
            },
            new PluginFormField
            {
                Name = "profile.maxSearchAttempts",
                Label = "Give up on an episode after this many searches",
                Type = PluginFormFieldType.Number,
                Value = profile.MaxSearchAttempts,
            },
            new PluginFormField
            {
                Name = "profile.excludeTerms",
                Label = "Forbidden terms, separated by commas",
                Value = string.Join(", ", profile.ExcludeTerms),
                Placeholder = "HDCAM, CAM, TS",
            });
    }

    private static PluginComponent Client(ClientLimits limits, bool dryRun)
    {
        return Section(
            "client",
            new PluginFormField
            {
                Name = "client.listenPort",
                Label = "Listen port (TCP and UDP)",
                Type = PluginFormFieldType.Number,
                Value = limits.ListenPort,
            },
            new PluginFormField
            {
                Name = "client.portMapping",
                Label = "Ask the router to open it",
                Type = PluginFormFieldType.Toggle,
                Value = limits.PortMapping,
            },
            new PluginFormField
            {
                Name = "client.maxDownloadRate",
                Label = "Maximum download, bytes per second — 0 is unlimited",
                Type = PluginFormFieldType.Number,
                Value = limits.MaxDownloadRate,
            },
            new PluginFormField
            {
                Name = "client.maxUploadRate",
                Label = "Maximum upload, bytes per second — 0 is unlimited",
                Type = PluginFormFieldType.Number,
                Value = limits.MaxUploadRate,
            },
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
            new PluginFormField
            {
                Name = "client.stallMinutes",
                Label = "Minutes with no progress and no peers before it counts as stalled",
                Type = PluginFormFieldType.Number,
                Value = limits.StallMinutes,
            },
            new PluginFormField
            {
                Name = "client.metadataTimeoutMinutes",
                Label = "Minutes to wait for a magnet's metadata",
                Type = PluginFormFieldType.Number,
                Value = limits.MetadataTimeoutMinutes,
            },
            new PluginFormField
            {
                Name = "client.maxConcurrentDownloads",
                Label = "Downloads at once",
                Type = PluginFormFieldType.Number,
                Value = limits.MaxConcurrentDownloads,
            },
            new PluginFormField
            {
                Name = "client.encryption",
                Label = "Encryption",
                Type = PluginFormFieldType.Select,
                Value = limits.Encryption.ToString(),
                Options =
                [
                    .. Enum.GetNames<EncryptionPolicy>()
                        .Select(name => new PluginFormOption { Label = name, Value = name }),
                ],
            },
            new PluginFormField
            {
                Name = "dryRun",
                Label = "Dry run — decide everything, download nothing",
                Type = PluginFormFieldType.Toggle,
                Value = dryRun,
            });
    }

    /// <summary>One entry of a list the owner picks from.</summary>
    private static PluginFormOption Choice(string value)
    {
        return new() { Label = value, Value = value };
    }

    /// <summary>
    /// One section of the page: its fields, and the button that saves them.
    /// </summary>
    /// <remarks>
    /// A form per section rather than one for the whole page, because a form
    /// posts only the fields it holds and the applier changes only what it is
    /// sent. Saving the folders leaves the quality profile exactly as it was,
    /// which is what lets a page be saved a piece at a time.
    /// </remarks>
    private static PluginComponent Section(string id, params PluginFormField[] fields)
    {
        return Ui.Form(
            id,
            "Save",
            PluginActionIntent.CallPlugin(SaveAction, null, PluginActionTransport.Rest),
            fields);
    }

    private static PluginComponent Indexers(Settings settings, HashSet<string> present)
    {
        return Ui.Detail(
            "indexers",
            "Own indexers",
            settings.Indexers.Count == 0 ? "None added." : null,
            null,
            [
                .. settings.Indexers.SelectMany(indexer => (PluginComponent[])
                [
                    Ui.Text($"indexer-{indexer.Id}", $"{indexer.Name} — {indexer.Address}"),
                    Secret($"indexer-{indexer.Id}-key", "API key", present.Contains(SettingsStore.IndexerApiKey(indexer.Id))),
                ]),
            ]);
    }

    private static PluginComponent Trackers(Settings settings, HashSet<string> present)
    {
        return Ui.Detail(
            "trackers",
            "Private trackers",
            settings.PrivateTrackers.Count == 0 ? "None added." : null,
            null,
            [
                .. settings.PrivateTrackers.SelectMany(tracker => (PluginComponent[])
                [
                    // The template, which carries {passkey} where the secret
                    // goes, so the address is showable and the secret is not in
                    // it to show.
                    Ui.Text($"tracker-{tracker.Id}", $"{tracker.Host} — {Or(tracker.AnnounceTemplate, "no announce URL")}"),
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
    private static PluginComponent Running(Settings settings)
    {
        return Ui.Detail(
            "run",
            "Run",
            settings.DryRun
                ? "Dry run is on: a cycle decides for every episode and hands nothing to the torrent client."
                : "A cycle looks for every missing episode and downloads what it settles on.",
            null,
            Ui.Button(
                "run-run",
                "Run now",
                PluginActionIntent.CallPlugin(RunAction, null, PluginActionTransport.Rest),
                variant: "primary"),
            Ui.Button(
                "run-stop",
                "Stop",
                PluginActionIntent.CallPlugin(StopAction, null, PluginActionTransport.Rest)));
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

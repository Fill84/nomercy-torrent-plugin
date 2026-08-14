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

    /// <summary>
    /// The page. <c>secretsSet</c> is the keys the secret store holds — names
    /// only, see the remarks above — and <c>problems</c> is why the last save
    /// was refused, if it was.
    /// </summary>
    public static PluginView Render(
        Settings settings,
        IReadOnlyCollection<string> secretsSet,
        IReadOnlyList<string> problems)
    {
        HashSet<string> present = new(secretsSet, StringComparer.Ordinal);

        return new()
        {
            Layout = PluginLayout.Form,
            Components =
            [
                .. problems.Select((string problem, int index) =>
                    PluginViews.Text($"problem-{index}", problem, "caption")),
                Folders(settings),
                CadenceSection(settings.Cadences),
                Quality(settings.Profile),
                Client(settings.Client),
                Indexers(settings, present),
                Trackers(settings, present),
                NotWiredYet(settings),
            ],
        };
    }

    private static PluginComponent Folders(Settings settings)
    {
        return PluginViews.Detail(
            "folders",
            "Folders",
            "Where downloads land, and where finished video is staged for the encoder.",
            null,
            PluginViews.Text("folders-incomplete", $"Incomplete: {Or(settings.IncompleteFolder, "not chosen")}"),
            PluginViews.Text("folders-intake", $"Intake: {Or(settings.IntakeFolder, "not chosen")}"));
    }

    private static PluginComponent CadenceSection(Cadences cadences)
    {
        return PluginViews.Detail(
            "cadences",
            "Cadences",
            // The owner is told, because the alternative is that they change a
            // cron, watch the old one keep firing, and conclude the setting
            // does not work. The server registers cadences once, when the
            // plugin loads.
            "A changed cadence takes effect when the server restarts, not before.",
            null,
            [
                .. cadences.All().Select((cadence, index) =>
                    PluginViews.Text($"cadence-{index}", $"{cadence.Name}: {cadence.Expression}")),
            ]);
    }

    private static PluginComponent Quality(Profile profile)
    {
        return PluginViews.Detail(
            "quality",
            "Quality",
            null,
            null,
            PluginViews.Text("quality-resolution", $"Maximum resolution: {profile.MaximumResolution}"),
            PluginViews.Text("quality-codec", $"Codec: {profile.Codec}"),
            PluginViews.Text(
                "quality-codec-tag",
                profile.CodecTagRequired
                    ? "An untagged release is refused."
                    : "Untagged releases are accepted, because no codec is wanted."),
            PluginViews.Text("quality-seeders", $"Minimum seeders: {profile.MinimumSeeders}"),
            PluginViews.Text(
                "quality-packs",
                profile.AllowSeasonPacks
                    ? $"Season packs from {profile.SeasonPackThreshold} gaps."
                    : "Season packs are never taken."),
            PluginViews.Text("quality-english", profile.EnglishOnly ? "English only." : "Any language."),
            PluginViews.Text("quality-specials", profile.IncludeSpecials ? "Specials included." : "Specials skipped."),
            PluginViews.Text("quality-attempts", $"Maximum search attempts: {profile.MaxSearchAttempts}"),
            PluginViews.Text(
                "quality-exclude",
                $"Forbidden terms: {Or(string.Join(", ", profile.ExcludeTerms), "none")}"));
    }

    private static PluginComponent Client(ClientLimits limits)
    {
        return PluginViews.Detail(
            "client",
            "Torrent client",
            null,
            null,
            PluginViews.Text("client-port", $"Listen port: {limits.ListenPort} (TCP and UDP)"),
            PluginViews.Text("client-mapping", limits.PortMapping ? "Port mapping on." : "Port mapping off."),
            PluginViews.Text("client-down", Rate("Maximum download", limits.MaxDownloadRate)),
            PluginViews.Text("client-up", Rate("Maximum upload", limits.MaxUploadRate)),
            PluginViews.Text("client-seed", $"Seed to {limits.SeedRatio} or {limits.SeedHours} h, whichever comes first."),
            PluginViews.Text("client-stall", $"A stall is {limits.StallMinutes} min with no progress and no peers."),
            PluginViews.Text("client-metadata", $"Metadata timeout: {limits.MetadataTimeoutMinutes} min"),
            PluginViews.Text("client-concurrent", $"At most {limits.MaxConcurrentDownloads} downloads at once."),
            PluginViews.Text("client-encryption", $"Encryption: {limits.Encryption}"),
            PluginViews.Text(
                "client-trackers",
                // Not "0 trackers": none has been chosen, which is a different
                // thing from a list that is empty by preference.
                limits.DefaultTrackers.Count == 0
                    ? "Default trackers: none chosen."
                    : $"Default trackers: {limits.DefaultTrackers.Count}"));
    }

    private static PluginComponent Indexers(Settings settings, HashSet<string> present)
    {
        return PluginViews.Detail(
            "indexers",
            "Own indexers",
            settings.Indexers.Count == 0 ? "None added." : null,
            null,
            [
                .. settings.Indexers.SelectMany(indexer => (PluginComponent[])
                [
                    PluginViews.Text($"indexer-{indexer.Id}", $"{indexer.Name} — {indexer.Address}"),
                    Secret($"indexer-{indexer.Id}-key", "API key", present.Contains(SettingsStore.IndexerApiKey(indexer.Id))),
                ]),
            ]);
    }

    private static PluginComponent Trackers(Settings settings, HashSet<string> present)
    {
        return PluginViews.Detail(
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
                    PluginViews.Text($"tracker-{tracker.Id}", $"{tracker.Host} — {Or(tracker.AnnounceTemplate, "no announce URL")}"),
                    Secret($"tracker-{tracker.Id}-passkey", "Passkey", present.Contains(SettingsStore.TrackerPasskey(tracker.Id))),
                ]),
            ]);
    }

    private static PluginComponent NotWiredYet(Settings settings)
    {
        return PluginViews.Detail(
            "run",
            "Run",
            // A control that answers with silence is indistinguishable from one
            // that started a cycle which then found nothing, and the owner
            // would wait for a result that was never coming.
            "Run, Stop and dry run do nothing yet: there is no pipeline behind them to start or stop.",
            null,
            PluginViews.Text("run-run", "Run"),
            PluginViews.Text("run-stop", "Stop"),
            PluginViews.Text("run-dry", settings.DryRun ? "Dry run: on" : "Dry run: off"));
    }

    /// <summary>Whether a secret is stored — never which one, and never what.</summary>
    private static PluginComponent Secret(string id, string label, bool isSet)
    {
        return PluginViews.Text(id, $"{label}: {(isSet ? "set" : "not set")}", "caption");
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

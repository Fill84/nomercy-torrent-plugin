using System.Globalization;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// What one source last did.
/// </summary>
/// <param name="Name">The site, as the catalogue names it.</param>
/// <param name="At">When it was last asked, or null when it never has been.</param>
/// <param name="Rows">How many releases it answered with.</param>
/// <param name="Refusal">
/// What it said when it refused, in its own words. Null when it did not refuse.
/// </param>
/// <param name="Duration">How long it took.</param>
/// <param name="NextAskable">
/// When it may be asked again. A source that is rate-limited is not broken, and
/// the difference is the whole reason this column exists.
/// </param>
public sealed record SourceReport(
    string Name,
    DateTimeOffset? At,
    int Rows,
    string? Refusal,
    TimeSpan Duration,
    DateTimeOffset? NextAskable);

/// <summary>
/// Every source, and what it last answered.
/// </summary>
/// <remarks>
/// <para>
/// <strong>G2.</strong> 0.3.4's health check attributed one source's page to
/// another and reported its own rate-limiting as a broken parser — so the owner
/// was told a site was broken when the plugin had simply asked it too often.
/// This page keeps the two apart: a refusal is in the site's own words, and
/// when a source may be asked again is a column of its own.
/// </para>
/// <para>
/// Nought rows is not a fault either. A site that answered and had nothing is a
/// working site, and the way to tell it from a broken one is the refusal beside
/// it being empty.
/// </para>
/// </remarks>
public static class SourcesView
{
    public const string TableId = "sources";

    public static PluginView Render(
        IReadOnlyList<SourceReport> sources,
        DateTimeOffset now,
        IReadOnlyList<SourceDefinition>? shipped = null,
        Settings? settings = null,
        IReadOnlyCollection<string>? secretsSet = null,
        bool advanced = false)
    {
        List<PluginComponent> page =
            [
                Ui.Text("sources-heading", "Sources", "title"),
                Ui.Text(
                    "sources-secondary",
                    "What each site last answered. A refusal is in the site's own words.",
                    "caption"),
                Ui.Table(
                    TableId,
                    [
                        new() { Key = "source", Label = "Source" },
                        new() { Key = "last", Label = "Last asked" },
                        new() { Key = "rows", Label = "Rows" },
                        new() { Key = "took", Label = "Took" },
                        new() { Key = "refusal", Label = "Refusal" },
                        new() { Key = "next", Label = "Askable again" },
                    ],
                    [
                        .. sources.Select(source => Ui.Row(
                            $"{TableId}-{source.Name}",
                            new Dictionary<string, object?>
                            {
                                ["source"] = source.Name,

                                // Never asked is not long ago, and nought would
                                // be a date in 1970.
                                ["last"] = source.At?.ToString("u", CultureInfo.InvariantCulture) ?? Never,
                                ["rows"] = source.At is null ? Unknown : source.Rows,
                                ["took"] = source.At is null ? Unknown : Took(source.Duration),

                                // Its own words, or nothing at all. A site that
                                // answered and had nothing to give is working.
                                ["refusal"] = source.Refusal ?? string.Empty,
                                ["next"] = Next(source, now),
                            })),
                    ],
                    "No source has been asked yet."),
        ];

        // The indexers of the owner live here now rather than on the settings
        // page: this is the page about sources, and an indexer is one.
        if (settings is not null)
        {
            page.Add(Indexers(settings, new(secretsSet ?? [], StringComparer.Ordinal)));
        }

        // Under advanced, because an owner who came to look at what answered
        // should not have to walk past a switch for every site.
        if (advanced && shipped is not null)
        {
            page.Add(Switches(shipped, settings));
        }

        return new()
        {
            Layout = PluginLayout.Wide,
            Components = [.. page],
        };
    }

    /// <summary>
    /// The indexers the owner added, and whether each has its key.
    /// </summary>
    /// <remarks>
    /// Moved here from the settings page by <c>S12-08</c>. The API key stays
    /// write-only: the page renders that one is set and never the value, and it
    /// is handed only the names of the secrets that exist, so it has no value
    /// it could render even by mistake.
    /// </remarks>
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
                    Ui.Text($"indexer-{indexer.Id}", $"{indexer.Name} - {indexer.Address}"),
                    Ui.Text(
                        $"indexer-{indexer.Id}-key",
                        $"API key: {(present.Contains(SettingsStore.IndexerApiKey(indexer.Id)) ? "set" : "not set")}",
                        "caption"),
                ]),
            ]);
    }

    /// <summary>
    /// One switch per shipped source, on unless the owner turned it off.
    /// </summary>
    /// <remarks>
    /// <c>DisabledDefaultSources</c> records what is off rather than what is
    /// on, so a source nobody has touched is simply absent from it. That is
    /// what keeps a source added by a later version enabled instead of
    /// silently missing from a list written before it existed.
    /// </remarks>
    private static PluginComponent Switches(IReadOnlyList<SourceDefinition> shipped, Settings? settings)
    {
        HashSet<string> off = new(
            settings?.DisabledDefaultSources ?? [],
            StringComparer.OrdinalIgnoreCase);

        return Ui.Form(
            "source-switches",
            "Save",
            PluginActionIntent.CallPlugin(SettingsView.SaveAction, null, PluginActionTransport.Rest),
            [
                .. shipped.Select(source => new PluginFormField
                {
                    Name = SettingsEdit.SourcePrefix + source.Name,
                    Label = source.Name,
                    Type = PluginFormFieldType.Toggle,
                    Value = !off.Contains(source.Name),
                }),
            ]);
    }

    /// <summary>What a source that has never been asked says.</summary>
    public const string Never = "never";

    /// <summary>What a number that is not known says instead of nought.</summary>
    private const string Unknown = "—";

    /// <summary>
    /// When it may be asked again, as a wait rather than a timestamp.
    /// </summary>
    /// <remarks>
    /// "In four minutes" is something the owner can act on; a time in UTC is
    /// something they have to work out. A source that is askable now says so,
    /// which is what tells a rate-limited site apart from a broken one.
    /// </remarks>
    /// <remarks>
    /// A clock time, never a distance from now: "in 3 min" is true for a minute
    /// and then quietly wrong, and nothing pushes to correct it because the
    /// passing of a minute changes nothing the plugin holds. The owner's
    /// decision of 11 September 2026.
    /// </remarks>
    private static string Next(SourceReport source, DateTimeOffset now)
    {
        if (source.NextAskable is not DateTimeOffset next || next <= now)
        {
            return "now";
        }

        return next.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>How long it took, at the resolution a person cares about.</summary>
    private static string Took(TimeSpan duration)
    {
        return duration.TotalSeconds >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0.0} s")
            : string.Create(CultureInfo.InvariantCulture, $"{duration.TotalMilliseconds:0} ms");
    }
}

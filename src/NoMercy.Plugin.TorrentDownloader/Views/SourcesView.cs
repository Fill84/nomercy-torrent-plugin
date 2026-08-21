using System.Globalization;
using NoMercy.Plugins.Abstractions;

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

    public static PluginView Render(IReadOnlyList<SourceReport> sources, DateTimeOffset now)
    {
        return new()
        {
            Layout = PluginLayout.ListDetail,
            Components =
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
            ],
        };
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
    private static string Next(SourceReport source, DateTimeOffset now)
    {
        if (source.NextAskable is not DateTimeOffset next || next <= now)
        {
            return "now";
        }

        TimeSpan wait = next - now;

        return wait.TotalMinutes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"in {wait.TotalMinutes:0} min")
            : string.Create(CultureInfo.InvariantCulture, $"in {wait.TotalSeconds:0} s");
    }

    /// <summary>How long it took, at the resolution a person cares about.</summary>
    private static string Took(TimeSpan duration)
    {
        return duration.TotalSeconds >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0.0} s")
            : string.Create(CultureInfo.InvariantCulture, $"{duration.TotalMilliseconds:0} ms");
    }
}

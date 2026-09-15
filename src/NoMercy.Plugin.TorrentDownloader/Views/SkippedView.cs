using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// What a show's settings or the blacklist refused, and why.
/// </summary>
/// <remarks>
/// <para>
/// <strong>G3.</strong> 0.3.4 listed raw parser output on its pages, so a
/// release already refused sat there looking like a candidate. This page is the
/// opposite: everything on it was refused, each with its show, its episode and
/// the reason it was refused for.
/// </para>
/// <para>
/// <strong>And nothing to press.</strong> <c>docs/specs/pages.md</c> § Skipped,
/// the owner's requirements of 15 September 2026: a refused release name offers
/// no button to download it anyway. What decides is the show's settings, and the
/// place to change them is the show's own form on the overview.
/// </para>
/// </remarks>
public static class SkippedView
{
    public const string TableId = "skipped";

    /// <summary>Where a page of refusals is asked for.</summary>
    /// <remarks>
    /// The page number rides in the address so that a page can be linked to,
    /// reloaded and gone back to. A page held in memory instead would put the
    /// owner back at the top every time the view refreshed, and this view
    /// refreshes whenever the journal moves.
    /// </remarks>
    public const string PageQuery = "page";

    /// <summary>How many refusals one page holds.</summary>
    /// <remarks>
    /// Fifty is what fits without scrolling forever and is few enough to draw
    /// at once. The whole list used to be drawn — 65,878 rows on the owner's
    /// server — and the page took most of a minute to open.
    /// </remarks>
    public const int PageSize = 50;

    public static PluginView Render(SkippedPage page)
    {
        IReadOnlyList<SkippedRelease> skipped = page.Rows;

        return new()
        {
            Layout = PluginLayout.Wide,
            Components =
            [
                Ui.Text("skipped-heading", "Skipped", "title"),
                Ui.Text(
                    "skipped-secondary",
                    "Release names a show's settings or the blacklist refused, and why. A show's settings are changed on the overview.",
                    "caption"),
                Ui.Table(
                    TableId,
                    [
                        new() { Key = "show", Label = "Show" },
                        new() { Key = "episode", Label = "Episode" },
                        new() { Key = "release", Label = "Release" },
                        new() { Key = "source", Label = "Source" },
                        new() { Key = "reason", Label = "Why it was refused" },
                    ],
                    [
                        .. skipped.Select((SkippedRelease one, int index) => Ui.Row(
                            $"{TableId}-{index}",
                            new Dictionary<string, object?>
                            {
                                // Said when it was not recorded, never blank.
                                ["show"] = one.ShowTitle is { Length: > 0 } show ? show : "show not recorded",
                                ["episode"] = Episode(one),
                                ["release"] = one.Title,

                                // A site that did not say which it was is not a
                                // site called nothing.
                                ["source"] = one.Source ?? "unknown",

                                // Never blank. A refusal with no reason is the
                                // one thing the owner opened this page to read.
                                ["reason"] = one.Reason,
                            })),
                    ],
                    "Nothing has been refused."),
                .. Paging(page),
            ],
        };
    }

    /// <summary>
    /// Where this page sits, and the way to the ones either side.
    /// </summary>
    /// <remarks>
    /// Drawn only when there is more than one page. A pair of dead buttons
    /// under a short list is furniture that says the plugin has more to show
    /// when it has not.
    /// </remarks>
    private static IEnumerable<PluginComponent> Paging(SkippedPage page)
    {
        if (page.Pages <= 1)
        {
            yield break;
        }

        // The count is the point of the line: a page of fifty out of sixty-five
        // thousand is a very different thing from fifty out of sixty, and the
        // owner cannot tell which they are looking at from the rows.
        yield return Ui.Text(
            $"{TableId}-range",
            $"Showing {page.First} to {page.Last} of {page.Total} refusals, page {page.Page} of {page.Pages}.",
            "caption");

        List<PluginComponent> controls = [];

        if (page.HasPrevious)
        {
            controls.Add(Ui.Button(
                $"{TableId}-previous",
                "Previous",
                PluginActionIntent.Navigate(Address(page.Page - 1))));
        }

        if (page.HasNext)
        {
            controls.Add(Ui.Button(
                $"{TableId}-next",
                "Next",
                PluginActionIntent.Navigate(Address(page.Page + 1))));
        }

        yield return Ui.Row($"{TableId}-paging", [.. controls]);
    }

    /// <summary>This page's own address, which is what makes it linkable.</summary>
    private static string Address(int page)
    {
        return page <= 1 ? Pages.SkippedRoute : $"{Pages.SkippedRoute}?{PageQuery}={page}";
    }

    /// <summary>Which episode it was refused for, as a person writes it.</summary>
    private static string Episode(SkippedRelease skipped)
    {
        return $"S{skipped.Episode.Season:00}E{skipped.Episode.Number:00}";
    }
}

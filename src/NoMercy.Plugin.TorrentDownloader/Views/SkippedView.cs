using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// What the profile or the blacklist refused, and why.
/// </summary>
/// <remarks>
/// <para>
/// <strong>G3.</strong> 0.3.4 listed raw parser output on its pages, so a
/// release the profile had already refused sat there looking like a candidate.
/// This page is the opposite: everything on it was refused, each with the
/// reason it was refused for, and the control to overrule that decision sits
/// next to it.
/// </para>
/// <para>
/// The reason is the whole page. "Skipped" on its own tells the owner nothing
/// they can act on — whether to widen the profile, clear a blacklist entry, or
/// leave it well alone — and that judgement is exactly what this page exists to
/// let them make.
/// </para>
/// </remarks>
public static class SkippedView
{
    public const string TableId = "skipped";

    /// <summary>The action that grabs one anyway.</summary>
    // A control's "method" is the path the client posts to:
    // plugins/{id}/{method}, straight through. Naming the action instead of the
    // route gave every button on every page a URL this plugin does not serve,
    // and nothing anyone pressed did anything at all.
    public const string AllowAction = "skipped/allow";

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
                    "Releases the profile or the blacklist refused. Allowing one grabs it as it is.",
                    "caption"),
                Ui.Table(
                    TableId,
                    [
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
                                ["episode"] = Episode(one),
                                ["release"] = one.Title,

                                // A site that did not say which it was is not a
                                // site called nothing.
                                ["source"] = one.Source ?? "unknown",

                                // Never blank. A refusal with no reason is the
                                // one thing the owner opened this page to read.
                                ["reason"] = one.Reason,
                            },

                            // The row carries the action rather than a column of
                            // buttons: allowing a release is a decision about
                            // that row, and the reason it was refused has to be
                            // read before it is taken.
                            Allow(one))),
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

    /// <summary>
    /// Overruling one refusal.
    /// </summary>
    /// <remarks>
    /// It names the episode as well as the title, because a release is allowed
    /// <em>for</em> an episode: the same file can be refused for one and be
    /// exactly right for another.
    /// </remarks>
    private static PluginActionIntent Allow(SkippedRelease skipped)
    {
        return PluginActionIntent.CallPlugin(
            AllowAction,
            new Dictionary<string, object?>
            {
                ["showId"] = skipped.Episode.ShowId,
                ["season"] = skipped.Episode.Season,
                ["episode"] = skipped.Episode.Number,
                ["title"] = skipped.Title,
            },

            // REST, because allowing a release has an answer: it was grabbed,
            // or it was not and the page says why. The hub is for what the
            // plugin will keep reporting on.
            PluginActionTransport.Rest);
    }

    /// <summary>Which episode it was refused for, as a person writes it.</summary>
    private static string Episode(SkippedRelease skipped)
    {
        return $"S{skipped.Episode.Season:00}E{skipped.Episode.Number:00}";
    }
}

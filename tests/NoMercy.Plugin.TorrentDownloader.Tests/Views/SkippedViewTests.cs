using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>
/// The Skipped page, rendered from a seeded store.
/// </summary>
public class SkippedViewTests
{
    /// <remarks>
    /// The reason is the whole page. "Skipped" on its own tells the owner
    /// nothing they can act on — whether to widen the profile, clear a
    /// blacklist entry, or leave it alone — and that judgement is what this
    /// page exists to let them make.
    /// </remarks>
    [Fact]
    public void EveryRefusalIsRenderedWithTheReasonItWasRefusedFor()
    {
        PluginView view = SkippedView.Render(Page(
            new SkippedRelease(Episode(6), "Silo S03E06 720p WEB", "LimeTorrents", "720p is below the profile's floor of 1080p"),
            new SkippedRelease(Episode(6), "Silo S03E06 1080p x264", "1337x", "2 seeders is below the minimum of 5"),
            new SkippedRelease(Episode(7), "Silo S03E07 1080p", null, "the title is blacklisted")));

        string page = string.Join(" ", [.. Rendered.Words(view), .. Rendered.EveryValue(view)]);

        Assert.Contains("below the profile's floor", page, StringComparison.Ordinal);
        Assert.Contains("below the minimum of 5", page, StringComparison.Ordinal);
        Assert.Contains("blacklisted", page, StringComparison.Ordinal);

        // Which episode each was refused for, and which site offered it.
        Assert.Contains("S03E06", page, StringComparison.Ordinal);
        Assert.Contains("S03E07", page, StringComparison.Ordinal);
        Assert.Contains("LimeTorrents", page, StringComparison.Ordinal);

        // A site that did not say which it was is not a site called nothing.
        Assert.Contains("unknown", page, StringComparison.Ordinal);
        Assert.DoesNotContain(Rendered.EveryValue(view), string.IsNullOrWhiteSpace);
    }

    /// <remarks>
    /// <c>docs/specs/pages.md</c> § Skipped: the page lists the release names a show's settings refused,
    /// each with its show, episode and reason, and a refused release name offers no button to download it
    /// anyway. The control to allow one used to sit on every row.
    /// </remarks>
    [Fact]
    public void ARefusedNameIsListedWithShowEpisodeAndReasonAndNoAllowButton()
    {
        PluginView view = SkippedView.Render(Page(
            new SkippedRelease(Episode(6), "Silo.S03E06.720p.WEB.H264-SYLiX", null, "720p is not 1080p.") { ShowTitle = "Silo" }));

        PluginComponent row = Rendered.ById(view, "skipped-0");

        Assert.Equal("Silo", row.Props["show"]);
        Assert.Equal("S03E06", row.Props["episode"]);
        Assert.Equal("720p is not 1080p.", row.Props["reason"]);

        Assert.Null(row.Action);
        Assert.DoesNotContain(Rendered.All(view), component => component.Action is not null);
        Assert.DoesNotContain(
            Rendered.All(view).SelectMany(component => component.Props.Values).OfType<IReadOnlyList<PluginTableAction>>(),
            buttons => buttons.Count > 0);
    }

    /// <remarks>
    /// Nothing refused is a page that says so. An empty table with no
    /// explanation reads as a page that failed to load.
    /// </remarks>
    [Fact]
    public void AnEmptyPageSaysNothingHasBeenRefused()
    {
        Assert.Contains(
            "Nothing has been refused.",
            string.Join(" ", [.. Rendered.Words(SkippedView.Render(Page())), .. Rendered.EveryValue(SkippedView.Render(Page()))]),
            StringComparison.Ordinal);
    }

    private static EpisodeKey Episode(int number)
    {
        return new(42, 3, number);
    }
    /// <summary>One page holding exactly these, which is what a test means.</summary>
    private static SkippedPage Page(params SkippedRelease[] refused)
    {
        return new(refused, refused.Length, 1, SkippedView.PageSize);
    }
}

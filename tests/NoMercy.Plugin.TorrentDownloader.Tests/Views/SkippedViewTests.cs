using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Storage;
using System.Text.Json;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
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
    /// Every row carries the control to overrule it, naming the episode as well
    /// as the title: a release is allowed <em>for</em> an episode, and the same
    /// file can be refused for one and be exactly right for another.
    /// </remarks>
    [Fact]
    public void EveryRowCarriesTheControlToAllowThatOneRelease()
    {
        PluginView view = SkippedView.Render(Page(
            new SkippedRelease(Episode(6), "Silo S03E06 720p WEB", "LimeTorrents", "720p is below the profile's floor")));

        // The one row, by its id: the heading and the column headers share the
        // page's prefix, and only the row itself carries an action.
        PluginComponent row = Rendered.ById(view, "skipped-0");

        Assert.NotNull(row.Action);
        Assert.Single(Rendered.All(view), one => one.Action is not null);

        // The intent as it really travels, rather than as the page draws it:
        // the action's name and everything it needs to act on this one release.
        string intent = JsonSerializer.Serialize(row.Action);

        Assert.Contains("skipped/allow", intent, StringComparison.Ordinal);
        Assert.Contains("Silo S03E06 720p WEB", intent, StringComparison.Ordinal);
        Assert.Contains("42", intent, StringComparison.Ordinal);
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

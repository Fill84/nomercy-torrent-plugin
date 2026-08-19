using System.Text.Json;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>
/// The Sources page, rendered from a seeded store.
/// </summary>
public class SourcesViewTests
{
    /// <remarks>
    /// Per source: what it last answered, how long it took, its refusal in its
    /// own words, and when it is next askable.
    /// </remarks>
    [Fact]
    public void EverySourceRendersWhatItLastAnsweredAndHowLongItTook()
    {
        PluginView view = SourcesView.Render(
        [
            new("LimeTorrents", Now.AddMinutes(-2), 40, null, TimeSpan.FromSeconds(1.4), null),
            new("1337x", Now.AddMinutes(-2), 0, null, TimeSpan.FromMilliseconds(320), null),
        ],
            Now);

        string page = string.Join(" ", [.. Rendered.Words(view), .. Rendered.EveryValue(view)]);

        Assert.Contains("LimeTorrents", page, StringComparison.Ordinal);
        Assert.Contains("1.4 s", page, StringComparison.Ordinal);
        Assert.Contains("320 ms", page, StringComparison.Ordinal);
        Assert.Contains(Rendered.EveryValue(view), one => one == "40");
    }

    /// <remarks>
    /// <strong>G2.</strong> 0.3.4 reported its own rate-limiting as a broken
    /// parser, so the owner was told a site was broken when the plugin had
    /// simply asked it too often. A refusal is the site's own words, and when
    /// it may be asked again is a column of its own — as a wait, because "in 4
    /// min" is something an owner can act on and a timestamp in UTC is
    /// something they have to work out.
    /// </remarks>
    [Fact]
    public void ARateLimitedSourceIsNotRenderedAsABrokenOne()
    {
        PluginView view = SourcesView.Render(
        [
            new("TorrentGalaxy", Now.AddMinutes(-1), 0, "HTTP 429 from the site", TimeSpan.FromSeconds(2), Now.AddMinutes(4)),
            new("EZTV", Now.AddMinutes(-1), 0, "the reader found no rows in the page", TimeSpan.FromSeconds(1), Now),
        ],
            Now);

        string page = string.Join(" ", [.. Rendered.Words(view), .. Rendered.EveryValue(view)]);

        // The site's own words, both of them, kept apart.
        Assert.Contains("HTTP 429 from the site", page, StringComparison.Ordinal);
        Assert.Contains("the reader found no rows in the page", page, StringComparison.Ordinal);

        // And the one that is waiting says how long, while the one that is not
        // says it can be asked now.
        Assert.Contains("in 4 min", page, StringComparison.Ordinal);
        Assert.Contains("now", page, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A site that answered and had nothing is a working site. Nought rows with
    /// no refusal beside it is exactly how the owner tells that from a site
    /// that is broken.
    /// </remarks>
    [Fact]
    public void NoughtRowsWithNoRefusalIsAWorkingSource()
    {
        PluginView view = SourcesView.Render(
            [new("YTS", Now.AddMinutes(-5), 0, null, TimeSpan.FromSeconds(1), null)],
            Now);

        Assert.Contains(Rendered.EveryValue(view), one => one == "0");

        // Differentially, against the same source with a refusal: the row is
        // the only thing that changes, so what is being asserted is that
        // nothing is claimed about why — rather than the page's own wording,
        // which mentions refusals whether or not there are any.
        string working = JsonSerializer.Serialize(Rendered.ById(view, "sources-YTS"));

        string broken = JsonSerializer.Serialize(
            Rendered.ById(
                SourcesView.Render(
                    [new("YTS", Now.AddMinutes(-5), 0, "HTTP 503 from the site", TimeSpan.FromSeconds(1), null)],
                    Now),
                "sources-YTS"));

        Assert.Contains("HTTP 503 from the site", broken, StringComparison.Ordinal);
        Assert.DoesNotContain("HTTP 503", working, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Never asked is not long ago, and nought would draw as a date in 1970.
    /// Its counts say they are not known rather than being drawn as nought,
    /// which would read as a source that answered with nothing.
    /// </remarks>
    [Fact]
    public void ASourceThatHasNeverBeenAskedSaysSoRatherThanShowingNought()
    {
        PluginView view = SourcesView.Render(
            [new("Nyaa", null, 0, null, TimeSpan.Zero, null)],
            Now);

        string page = string.Join(" ", Rendered.EveryValue(view));

        Assert.Contains(SourcesView.Never, page, StringComparison.Ordinal);
        Assert.DoesNotContain(Rendered.EveryValue(view), one => one == "0");
        Assert.DoesNotContain("1970", page, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Nothing asked yet is a page that says so, rather than an empty table.
    /// </remarks>
    [Fact]
    public void AnEmptyPageSaysNoSourceHasBeenAsked()
    {
        Assert.Contains(
            "No source has been asked yet.",
            string.Join(" ", Rendered.EveryValue(SourcesView.Render([], Now))),
            StringComparison.Ordinal);
    }

    private static DateTimeOffset Now => new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
}

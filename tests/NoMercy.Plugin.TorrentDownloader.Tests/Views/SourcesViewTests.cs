using System.Globalization;
using System.Text.Json;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
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

        // And the one that is waiting says when it can be asked, as a clock
        // time rather than a distance from now — which goes stale the minute
        // after it is drawn, with nothing to push a correction. The one that is
        // not waiting says it can be asked now.
        Assert.Contains(
            Now.AddMinutes(4).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            page,
            StringComparison.Ordinal);
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

    /// <summary>The shipped catalogue, as far as this page is concerned.</summary>
    private static readonly SourceDefinition[] Shipped =
    [
        new("PreDB", "rss", "https://predb.me/?rss=1"),
        new("The Pirate Bay", "apibay", "https://apibay.org/q.php?q={query}&cat="),
        new("Nyaa", "torrent-rss", "https://nyaa.si/?page=rss&q={query}"),
    ];

    /// <remarks>
    /// <para>
    /// <strong>Every source is on until the owner says otherwise</strong>, and
    /// every one of them can be switched off. Until now nothing on any page
    /// wrote <c>DisabledDefaultSources</c> at all: the setting existed, the
    /// chain read it, and there was no way to put a name into it.
    /// </para>
    /// <para>
    /// Under advanced, because an owner who came to change a folder should not
    /// have to walk past a switch for every site the plugin ships with.
    /// </para>
    /// </remarks>
    [Fact]
    public void EverySourceHasASwitchAndTheyAreAllOnByDefault()
    {
        PluginView closed = SourcesView.Render([], Now, Shipped, new Settings());

        Assert.DoesNotContain("source-switches", Rendered.All(closed).Select(one => one.Id));

        PluginView view = SourcesView.Render([], Now, Shipped, new Settings(), advanced: true);

        PluginComponent form = Rendered.ById(view, "source-switches");

        IReadOnlyList<PluginFormField> fields =
            form.Props.GetValueOrDefault("fields") is IEnumerable<PluginFormField> found
                ? [.. found]
                : throw new InvalidOperationException("'source-switches' is not a form with fields.");

        Assert.Equal(Shipped.Length, fields.Count);

        foreach (PluginFormField field in fields)
        {
            Assert.Equal(PluginFormFieldType.Toggle, field.Type);
            Assert.Equal(true, field.Value);
        }

        Assert.Contains("source.PreDB", fields.Select(field => field.Name));
    }

    /// <remarks>
    /// And the switch reaches the setting. A control that renders and writes
    /// nowhere is the fault this page had: the owner flicks it, watches it
    /// save, and the source keeps being asked.
    /// </remarks>
    [Fact]
    public void ASourceSwitchedOffIsWrittenToDisabledDefaultSources()
    {
        Settings settings = new();

        Assert.Empty(SettingsEdit.Apply(
            settings,
            new Dictionary<string, string?> { ["source.PreDB"] = "false" }));

        Assert.Contains("PreDB", settings.DisabledDefaultSources);

        // And back on again, which is the half that gets forgotten.
        Assert.Empty(SettingsEdit.Apply(
            settings,
            new Dictionary<string, string?> { ["source.PreDB"] = "true" }));

        Assert.DoesNotContain("PreDB", settings.DisabledDefaultSources);
    }

    /// <remarks>
    /// <para>
    /// A shipped source is not the owner's to edit: its address, its reader and
    /// its pacing are this plugin's, measured against a real capture, and a
    /// page that let them be typed over would be a page that breaks a reader.
    /// </para>
    /// <para>
    /// So no address is drawn and no editor is offered. Asserted on the
    /// addresses themselves rather than on the absence of a component, because
    /// what matters is that the string is not on the page however it got there.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASourceInUseIsNotEditableAndItsAddressIsNotShown()
    {
        PluginView view = SourcesView.Render(
            [new SourceReport("PreDB", Now, 21, null, TimeSpan.FromSeconds(1), null)],
            Now,
            Shipped,
            new Settings(),
            advanced: true);

        string page = string.Join(
            " ",
            [.. Rendered.Words(view), .. Rendered.EveryValue(view).Select(value => value?.ToString())]);

        foreach (SourceDefinition source in Shipped)
        {
            Assert.DoesNotContain(source.Url, page, StringComparison.Ordinal);
        }

        // The switch is the only control a shipped source gets.
        IReadOnlyList<PluginFormField> fields =
            Rendered.ById(view, "source-switches").Props.GetValueOrDefault("fields")
                    is IEnumerable<PluginFormField> found
                ? [.. found]
                : [];

        Assert.All(fields, field => Assert.Equal(PluginFormFieldType.Toggle, field.Type));
    }

    /// <remarks>
    /// <para>
    /// Not set is its own answer, and it is the one that explains why an
    /// indexer is refusing every request. Moved here with the block by
    /// <c>S12-08</c>: it guarded the settings page, and the guarantee is the
    /// same wherever the block is drawn.
    /// </para>
    /// <para>
    /// The key is write-only. The page is handed only the names of the secrets
    /// that exist, so it can say one is set and has no value it could render
    /// even by accident.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnIndexerWithNoApiKeySaysNotSet()
    {
        Settings settings = new();
        settings.Indexers.Add(new() { Id = "own-1", Name = "Mine", Address = "https://x/?q={query}" });

        PluginView view = SourcesView.Render([], Now, Shipped, settings);

        Assert.Contains("not set", string.Join(" ", Rendered.Words(view)), StringComparison.OrdinalIgnoreCase);
    }
}

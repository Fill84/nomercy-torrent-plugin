using NoMercy.Plugin.TorrentDownloader.Bittorrent;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

public class SettingsViewTests
{
    /// <remarks>
    /// A passkey and an API key are secrets. The page has to answer "is one
    /// set?" without ever being able to answer "what is it?", so it is handed
    /// the key names that exist and never the values — there is no code path
    /// from this view to the secret store at all.
    /// </remarks>
    [Fact]
    public void AStoredPasskeyAndApiKeyRenderAsSetAndNeverAsTheirValue()
    {
        Settings settings = new();
        settings.Indexers.Add(new() { Id = "own-1", Name = "Mine", Address = "https://x/?q={query}" });
        settings.PrivateTrackers.Add(new() { Id = "trk-1", Host = "tracker.example" });

        PluginView view = SettingsView.Render(
            settings,
            [SettingsStore.IndexerApiKey("own-1"), SettingsStore.TrackerPasskey("trk-1")],
            []);

        string page = string.Join(" ", Rendered.Words(view));

        Assert.Contains("set", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2", page, StringComparison.OrdinalIgnoreCase);
        Assert.All(
            Rendered.All(view),
            component => Assert.DoesNotContain(
                "passkey=",
                string.Join(" ", component.Props.Values.Select(value => value?.ToString() ?? string.Empty)),
                StringComparison.OrdinalIgnoreCase));
    }

    /// <remarks>
    /// Not set is its own answer, and it is the one that explains why a private
    /// tracker is refusing every request.
    /// </remarks>
    [Fact]
    public void AnIndexerWithNoApiKeySaysNotSet()
    {
        Settings settings = new();
        settings.Indexers.Add(new() { Id = "own-1", Name = "Mine", Address = "https://x/?q={query}" });

        PluginView view = SettingsView.Render(settings, [], []);

        Assert.Contains("not set", string.Join(" ", Rendered.Words(view)), StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// A control that does nothing has to say so. A Run button that answers
    /// with silence is indistinguishable from one that started a cycle which
    /// then found nothing, and the owner would wait for a result that was never
    /// coming.
    /// </remarks>
    [Fact]
    public void RunStopAndDryRunSayThatTheyDoNothingYet()
    {
        PluginView view = SettingsView.Render(new(), [], []);

        string page = string.Join(" ", Rendered.Words(view));

        Assert.Contains("Run", page, StringComparison.Ordinal);
        Assert.Contains("Stop", page, StringComparison.Ordinal);
        Assert.Contains("Dry run", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nothing", page, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// Changing a cron changes nothing until the server restarts, because
    /// cadences are registered once when the plugin loads. An owner not told
    /// that watches the old schedule keep firing and concludes the setting is
    /// broken.
    /// </remarks>
    [Fact]
    public void TheCadenceSectionSaysAChangeNeedsARestart()
    {
        PluginView view = SettingsView.Render(new(), [], []);

        Assert.Contains("restart", string.Join(" ", Rendered.Words(view)), StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// The reason a save was refused belongs on the page beside the field, not
    /// in a log the owner has no reason to open.
    /// </remarks>
    [Fact]
    public void ARefusalIsShownOnThePage()
    {
        PluginView view = SettingsView.Render(
            new(),
            [],
            ["The hour is 0 to 23, and '24' is not."]);

        Assert.Contains(
            "The hour is 0 to 23, and '24' is not.",
            string.Join(" ", Rendered.Words(view)),
            StringComparison.Ordinal);
    }

    /// <remarks>
    /// The page is a form, so a client knows to give it a form's shell rather
    /// than the ordinary one.
    /// </remarks>
    [Fact]
    public void TheSettingsPageIsAForm()
    {
        Assert.Equal(PluginLayout.Form, SettingsView.Render(new(), [], []).Layout);
    }

    /// <remarks>
    /// <para>
    /// The owner asked for exactly this: try UPnP, then NAT-PMP, and when both
    /// fail say plainly that the port needs forwarding by hand. A client nobody
    /// can dial downloads from the few peers it reaches out to and seeds to
    /// nobody — and on this network neither protocol answers at all, so this is
    /// the message that gets seen rather than an edge case.
    /// </para>
    /// <para>
    /// The router's own words go underneath, because "port mapping failed" and
    /// "your router has UPnP turned off" are different problems and only one is
    /// worth walking to the cupboard for.
    /// </para>
    /// </remarks>
    [Fact]
    public void APortThatCouldNotBeMappedTellsTheOwnerToForwardItByHand()
    {
        PluginView view = SettingsView.Render(
            new(),
            [],
            [],
            new(MappedBy.Nothing, 51413, "UPnP: no device answered the search; NAT-PMP: the gateway did not answer"));

        string page = string.Join(" ", [.. Rendered.Words(view), .. Rendered.EveryValue(view)]);

        Assert.Contains("51413", page, StringComparison.Ordinal);
        Assert.Contains("by hand", page, StringComparison.Ordinal);
        Assert.Contains("TCP and UDP", page, StringComparison.Ordinal);
        Assert.Contains("no device answered the search", page, StringComparison.Ordinal);
    }

    /// <remarks>
    /// And nothing at all when the port is open, or when nothing has tried yet.
    /// A page that said "the port is fine" on every load would be one more
    /// thing to read past.
    /// </remarks>
    [Fact]
    public void APortThatIsOpenSaysNothingAboutItself()
    {
        foreach (PortMapResult? mapping in new PortMapResult?[] { null, new(MappedBy.Upnp, 51413, null) })
        {
            PluginView view = SettingsView.Render(new(), [], [], mapping);

            Assert.DoesNotContain(
                "by hand",
                string.Join(" ", [.. Rendered.Words(view), .. Rendered.EveryValue(view)]),
                StringComparison.Ordinal);
        }
    }
}

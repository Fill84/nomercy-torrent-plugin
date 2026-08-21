using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>
/// Every action is an endpoint <em>and</em> a control on the page named.
/// </summary>
/// <remarks>
/// docs/08-ui.md § Actions says both, and for a while only the endpoints
/// existed. An action nothing on the page can reach is an action nobody has —
/// the owner would have to know the address and post to it by hand.
/// </remarks>
public class ControlsOnPagesTests
{
    /// <remarks>
    /// Pause and resume are one control that says which it is, because a
    /// torrent is one or the other and a page offering both would have the
    /// owner guessing which applied.
    /// </remarks>
    [Theory]
    [InlineData(TorrentState.Downloading, "pause", "Pause")]
    [InlineData(TorrentState.Paused, "resume", "Resume")]
    public void TheDownloadsPageOffersPauseOrResumeAsTheTorrentStands(
        TorrentState state,
        string verb,
        string label)
    {
        PluginView page = DownloadsView.Render([Row(state)]);

        PluginComponent button = Rendered.ById(page, $"downloads-pause-{Hash}");

        // The path the client posts to, spelled out. Asserting the constant
        // against itself is what let every one of these name a route that does
        // not exist.
        Assert.Equal($"downloads/{Hash}/{verb}", Called(button));
        Assert.Contains(label, string.Join(" ", Rendered.Words(page)), StringComparison.Ordinal);
    }

    /// <remarks>
    /// Cancelling deletes what has been downloaded so far, so the contract
    /// carries the confirmation. Leaving it to the client would have one asking
    /// and another not, and the one that did not would be the one the owner was
    /// using.
    /// </remarks>
    [Fact]
    public void CancellingIsOfferedAndIsConfirmedBeforeItRuns()
    {
        PluginView page = DownloadsView.Render([Row(TorrentState.Downloading)]);

        PluginComponent button = Rendered.ById(page, $"downloads-cancel-{Hash}");

        Assert.Equal($"downloads/{Hash}/cancel", Called(button));

        PluginConfirmation confirm = Assert.IsType<PluginConfirmation>(button.Action!.Confirm);

        Assert.True(confirm.Destructive);
        Assert.Contains("Silo.S03E06", confirm.Message!, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A torrent added by hand needs somewhere to paste it, so the control is a
    /// form and not a button. A button with nothing to type into would be an
    /// action the owner cannot use.
    /// </remarks>
    [Fact]
    public void ATorrentCanBeAddedByHandFromThePage()
    {
        PluginView page = DownloadsView.Render([]);

        PluginComponent form = Rendered.ById(page, "downloads-add");

        Assert.Equal(PluginComponentType.Form, form.Component);

        // The action is on the button inside, not on the card: a card that
        // carries one is drawn as a button around the box it holds.
        Assert.Equal("downloads", Called(Rendered.ById(page, "downloads-add-submit")));
        // Somewhere to paste it. A form with no field is a button by another
        // name, and the owner would have nothing to type the magnet into.
        Assert.Contains("source", string.Join(" ", Rendered.EveryValue(page)), StringComparison.Ordinal);
    }

    /// <remarks>
    /// Looking for one episode now is a decision about that row, and the
    /// attempts beside it are what an owner reads before taking it.
    /// </remarks>
    [Fact]
    public void TheQueueOffersLookingForOneEpisodeNow()
    {
        PluginView page = QueueView.Render(
            [
                new(new(41, 3, 6), "Silo", 2021, LibraryKind.Television, null, null, EpisodeState.Missing),
            ]);

        PluginComponent row = Assert.Single(
            Rendered.All(page),
            one => one.Id.StartsWith($"{QueueView.LookingTableId}-", StringComparison.Ordinal)
                   && one.Action is not null);

        Assert.Equal("queue/search", Called(row));

        // The episode travels with it. A control that named the action and not
        // which row it was on would search for whatever the server felt like.
        Assert.Equal(41, Sent(row)["showId"]);
        Assert.Equal(6, Sent(row)["episode"]);
    }

    private const string Hash = "0123456789ABCDEF0123456789ABCDEF01234567";

    /// <summary>Which plugin method a control asks for.</summary>
    private static string Called(PluginComponent control)
    {
        return Assert.IsType<string>(
            Assert.IsType<PluginActionIntent>(control.Action).Payload["method"]);
    }

    /// <summary>What a control sends with the call.</summary>
    private static IReadOnlyDictionary<string, object?> Sent(PluginComponent control)
    {
        return Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<PluginActionIntent>(control.Action).Payload["payload"]);
    }

    private static DownloadRow Row(TorrentState state)
    {
        return new(
            new(Hash, $"magnet:?xt=urn:btih:{Hash}", "Silo.S03E06.1080p.WEB.H264-CAKES", GrabState.Downloading),
            new(
                Hash,
                "Silo.S03E06.1080p.WEB.H264-CAKES",
                state,
                BytesDone: 100,
                BytesTotal: 200,
                DownloadRateBytesPerSecond: 1,
                UploadRateBytesPerSecond: 0,
                Peers: 2,
                Seeds: 1,
                Ratio: 0.1,
                Eta: null,
                Error: null),
            @"C:\downloads");
    }
}

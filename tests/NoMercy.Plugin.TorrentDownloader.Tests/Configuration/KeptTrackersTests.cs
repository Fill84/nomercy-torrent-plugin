using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Configuration;

/// <summary>
/// The trackers the plugin has come across, kept in the owner's settings.
/// </summary>
/// <remarks>
/// The owner's decision, 20 August 2026: the default list is everything this
/// plugin meets rather than something anybody types in, and it travels with
/// every grab. Kept in the settings so it survives a restart — a list held in
/// memory would be relearned from nothing every time the server came up.
/// </remarks>
public class KeptTrackersTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nomercy-trackers-" + Guid.NewGuid().ToString("n")[..8]);

    [Fact]
    public async Task WhatACycleCameAcrossIsInTheSettingsAfterwards()
    {
        using TorrentDownloaderPlugin plugin = Initialised();

        await Saved(plugin, []);

        await plugin.KeepTrackersAsync(
            await plugin.Settings.LoadAsync(CancellationToken.None),
            ["udp://open.example:1337/announce", "http://tracker.example/announce"],
            CancellationToken.None);

        Assert.Equal(
            ["udp://open.example:1337/announce", "http://tracker.example/announce"],
            (await plugin.Settings.LoadAsync(CancellationToken.None)).Client.DefaultTrackers);
    }

    /// <remarks>
    /// And a second cycle that meets the same ones changes nothing. The same
    /// tracker announced twice per torrent is one that bans this client, and a
    /// settings file rewritten every six hours with the same contents is a
    /// write that says nothing.
    /// </remarks>
    [Fact]
    public async Task MeetingTheSameOnesAgainChangesNothing()
    {
        using TorrentDownloaderPlugin plugin = Initialised();

        await Saved(plugin, ["udp://open.example:1337/announce"]);

        await plugin.KeepTrackersAsync(
            await plugin.Settings.LoadAsync(CancellationToken.None),
            ["udp://open.example:1337/announce"],
            CancellationToken.None);

        Assert.Equal(
            ["udp://open.example:1337/announce"],
            (await plugin.Settings.LoadAsync(CancellationToken.None)).Client.DefaultTrackers);
    }

    /// <remarks>
    /// <strong>A passkey is never kept.</strong> The owner's own tracker
    /// carries their key in its address and this list goes out with every grab,
    /// so keeping one would hand their credentials to every public swarm they
    /// download from — and print them on the Settings page.
    /// </remarks>
    [Fact]
    public async Task TheOwnersOwnTrackerIsNeverKept()
    {
        using TorrentDownloaderPlugin plugin = Initialised();

        Settings settings = new()
        {
            IncompleteFolder = _folder,
            IntakeFolder = _folder,
        };

        settings.PrivateTrackers.Add(new()
        {
            Id = "trk-1",
            Host = "tracker.private.example",
            AnnounceTemplate = "https://tracker.private.example/announce?passkey={passkey}",
        });

        await plugin.Settings.SaveAsync(settings, CancellationToken.None);

        await plugin.KeepTrackersAsync(
            await plugin.Settings.LoadAsync(CancellationToken.None),
            [
                "https://tracker.private.example/announce?passkey=a1b2c3d4e5f6",
                "https://tracker.private.example/announce",
                "udp://open.example:1337/announce",
            ],
            CancellationToken.None);

        IReadOnlyList<string> kept =
            (await plugin.Settings.LoadAsync(CancellationToken.None)).Client.DefaultTrackers;

        Assert.Equal(["udp://open.example:1337/announce"], kept);
        Assert.All(kept, one => Assert.DoesNotContain("a1b2c3d4e5f6", one, StringComparison.Ordinal));
    }

    private TorrentDownloaderPlugin Initialised()
    {
        TorrentDownloaderPlugin plugin = new();

        plugin.Initialize(new FakePluginContext { DataFolderPath = _folder });

        return plugin;
    }

    private async Task Saved(TorrentDownloaderPlugin plugin, IReadOnlyList<string> trackers)
    {
        Settings settings = new()
        {
            IncompleteFolder = _folder,
            IntakeFolder = _folder,
        };

        settings.Client.DefaultTrackers = [.. trackers];

        await plugin.Settings.SaveAsync(settings, CancellationToken.None);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        TemporaryFolder.Forget(_folder);
    }
}

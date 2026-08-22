using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// Permission is asked for every host the plugin will actually reach.
/// </summary>
/// <remarks>
/// <para>
/// <strong>C2, and this is the second time.</strong> The request covered
/// <c>settings.Indexers</c> — the indexers the owner added themselves — while
/// the pipeline searched the shipped catalogue as well. On a default install
/// the owner has added none, so <em>nothing was ever asked for</em>: the
/// dashboard had no request to show, no host was ever granted, and every
/// shipped source refused itself with "the server has not granted access".
/// </para>
/// <para>
/// The plugin ran on its cadence and found nothing, and the page said the
/// sources had refused — which reads exactly like the sites turning us away.
/// </para>
/// <para>
/// Declaring a host in the manifest is not being granted it. The manifest says
/// what the plugin may ask for; the owner still has to say yes, once, per host,
/// and they cannot say yes to a question nobody asked.
/// </para>
/// </remarks>
public class EveryHostIsAskedForTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nomercy-grants-" + Guid.NewGuid().ToString("n")[..8]);

    [Fact]
    public async Task AnOwnerWhoHasAddedNothingIsStillAskedForTheShippedSources()
    {
        FakeGrants grants = new();

        using TorrentDownloaderPlugin plugin = await Configured(grants);

        await plugin.ExecuteAsync(JobNames.Search, CancellationToken.None);

        Assert.NotEmpty(grants.Requested);

        // Named, because these are the ones a default install searches and the
        // ones the owner saw refused.
        Assert.Contains(grants.Requested, request => request.Host == "nyaa.si");
        Assert.Contains(grants.Requested, request => request.Host == "www.1337x.to");
    }

    /// <remarks>
    /// The reason is what the owner reads when they decide, so it names the
    /// source rather than the plugin. "May this plugin reach the internet" is
    /// not a question anybody can answer well.
    /// </remarks>
    [Fact]
    public async Task EveryRequestSaysWhichSourceItIsFor()
    {
        FakeGrants grants = new();

        using TorrentDownloaderPlugin plugin = await Configured(grants);

        await plugin.ExecuteAsync(JobNames.Search, CancellationToken.None);

        // The source, by name. A host two sources share names both, so
        // refusing it is a decision the owner makes about all of them.
        Assert.Contains(
            grants.Requested,
            request => request.Host == "nyaa.si" && request.Reason.Contains("Nyaa", StringComparison.Ordinal));

        Assert.All(
            grants.Requested,
            request => Assert.False(string.IsNullOrWhiteSpace(request.Reason)));
    }

    /// <remarks>
    /// A host already granted is not asked for again. The store does not queue
    /// a second prompt, but a plugin that asked on every cadence would be
    /// writing a request every six hours for something already settled.
    /// </remarks>
    [Fact]
    public async Task AHostAlreadyGrantedIsNotAskedForAgain()
    {
        FakeGrants grants = new();
        grants.Grant("nyaa.si");

        using TorrentDownloaderPlugin plugin = await Configured(grants);

        await plugin.ExecuteAsync(JobNames.Search, CancellationToken.None);

        Assert.DoesNotContain(grants.Requested, request => request.Host == "nyaa.si");
    }

    private async Task<TorrentDownloaderPlugin> Configured(FakeGrants grants)
    {
        Directory.CreateDirectory(_folder);

        TorrentDownloaderPlugin plugin = new();

        plugin.Initialize(new FakePluginContext
        {
            DataFolderPath = _folder,
            Permits = grants,
            Shelves = new(),
        });

        await plugin.Settings.SaveAsync(
            new Settings
            {
                IncompleteFolder = _folder,
                IntakeFolder = _folder,

                // Nothing of the owner's own, which is every fresh install.
                DryRun = true,
            },
            CancellationToken.None);

        return plugin;
    }

    public void Dispose()
    {
        TemporaryFolder.Forget(_folder);
        GC.SuppressFinalize(this);
    }
}

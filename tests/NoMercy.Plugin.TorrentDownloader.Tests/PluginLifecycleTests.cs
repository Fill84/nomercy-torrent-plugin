// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

public class PluginLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    // Written through the real store rather than as hand-rolled JSON, so this seeds
    // whatever shape FileDownloadStore actually persists. The file name is spelled out
    // here on purpose: it is the one fact the plugin and this test have to agree on, and
    // a rename that forgets one of them empties the page rather than failing to build.
    private static async Task SeedStoreAsync(FakePluginContext context)
    {
        FileDownloadStore store = new(Path.Combine(context.DataFolderPath, "downloads.json"));

        await store.AddGrabAsync(
            new Grab
            {
                InfoHash = "abc",
                Key = new EpisodeKey(1, 1, 1),
                ReleaseTitle = "Some.Show.S01E01.1080p",
                Indexer = "site-a",
                GrabbedAt = Now,
            },
            CancellationToken.None);

        await store.RecordTransferAsync(
            new Transfer { InfoHash = "abc", BytesDone = 500, BytesTotal = 1000, Peers = 8, UpdatedAt = Now },
            CancellationToken.None);

        await store.RefreshWantedAsync(
            [new WantedEpisode { Key = new EpisodeKey(1, 1, 2), ShowTitle = "Some Show" }],
            CancellationToken.None);
    }

    // Matches SettingsViewTests' own Flatten: GetViewAsync's error path nests its EmptyState
    // inside a container, so a top-level-only check would miss it just as it would there.
    private static IEnumerable<PluginComponent> Flatten(IEnumerable<PluginComponent> components)
    {
        foreach (PluginComponent component in components)
        {
            yield return component;

            foreach (PluginComponent descendant in Flatten(component.Items))
            {
                yield return descendant;
            }
        }
    }

    [Fact]
    public void Initialize_DoesNotThrow()
    {
        TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();

        Action act = () => plugin.Initialize(context);

        act.Should().NotThrow();
    }

    [Fact]
    public void Initialize_DoesNoIoAndReadsNoConfiguration()
    {
        TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();

        plugin.Initialize(context);

        context.Configuration.Reads.Should().Be(0);
        context.Secrets.Reads.Should().Be(0);
    }

    [Fact]
    public void Initialize_DoesNotTouchTheNetwork()
    {
        TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();

        plugin.Initialize(context);

        context.HttpHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_IsSafeBeforeInitialize()
    {
        TorrentDownloaderPlugin plugin = new();

        Action act = plugin.Dispose;

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        TorrentDownloaderPlugin plugin = new();
        plugin.Initialize(new FakePluginContext());
        plugin.Dispose();

        Action act = plugin.Dispose;

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Dispose_CancelsATickStartedBeforeIt()
    {
        TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        TaskCompletionSource<bool> readGate = new();
        context.Configuration.Stored = new TorrentDownloaderSettings();
        context.Configuration.ReadGate = readGate;
        plugin.Initialize(context);

        Task tick = plugin.ExecuteAsync(JobNames.Transfers, CancellationToken.None);
        plugin.Dispose();
        readGate.TrySetResult(true);

        Func<Task> act = () => tick;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteAsync_AfterDisposeDoesNotRun()
    {
        TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        RecordingLogger logger = new();
        context.Logger = logger;
        plugin.Initialize(context);
        plugin.Dispose();

        Func<Task> act = () => plugin.ExecuteAsync(JobNames.Transfers, CancellationToken.None);

        await act.Should().ThrowAsync<ObjectDisposedException>();
        logger.Messages.Should().BeEmpty("a tick after Dispose must never reach the job body");
    }

    [Fact]
    public async Task GetViewAsync_AfterDisposeDoesNotThrow()
    {
        TorrentDownloaderPlugin plugin = new();
        plugin.Initialize(new FakePluginContext());
        plugin.Dispose();

        Func<Task<PluginView>> act = () => plugin.GetViewAsync(new PluginViewRequest { Route = "/settings" }, CancellationToken.None);

        PluginView view = (await act.Should().NotThrowAsync()).Which;

        // Not merely "did not throw" - a disposed plugin must not run the live view at all,
        // so this checks for the dedicated unavailable signal rather than accepting whatever
        // the ordinary settings tree happens to contain (which, with no indexers configured,
        // also has an EmptyState and would pass without the guard this test exists to prove).
        view.Components.Should().Contain(component => component.Id == "settings-unavailable");
    }

    [Fact]
    public async Task SaveSettingsAsync_AfterDisposeReturnsFailureWithoutThrowingOrTouchingTheStore()
    {
        TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        context.Configuration.Stored = new TorrentDownloaderSettings();
        plugin.Initialize(context);
        plugin.Dispose();

        SaveSettingsRequest request = new()
        {
            TransfersCron = "*/5 * * * *",
            FeedCron = "*/15 * * * *",
            SearchCron = "0 */6 * * *",
            MaintenanceCron = "0 4 * * *",
        };
        Func<Task<SaveSettingsOutcome>> act = () => plugin.SaveSettingsAsync(request, CancellationToken.None);

        SaveSettingsOutcome outcome = (await act.Should().NotThrowAsync()).Which;

        // A request racing Dispose is treated the same as GetViewAsync's race, not as
        // ExecuteAsync's (which throws): the request may legitimately overlap teardown, so it
        // gets a clean failure rather than an exception into ASP.NET Core's pipeline, and
        // nothing it submitted reaches configuration. The request above is otherwise a
        // perfectly valid general-form submission, so the only thing that can fail it is the
        // disposed guard - asserting on its specific message (not just "failed") is what keeps
        // this from passing for an unrelated validation reason.
        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Be("Torrent Downloader is unavailable.");
        context.Configuration.SavedObjects.Should().BeEmpty();
    }

    [Fact]
    public async Task GetViewAsync_WhenConfigurationReadThrows_RendersErrorViewInsteadOfThrowing()
    {
        TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        RecordingLogger logger = new();
        context.Logger = logger;
        context.Configuration.Stored = new TorrentDownloaderSettings();
        context.Configuration.ThrowOnRead = new JsonException("truncated settings file");
        plugin.Initialize(context);

        Func<Task<PluginView>> act = () => plugin.GetViewAsync(new PluginViewRequest { Route = "/settings" }, CancellationToken.None);

        PluginView view = (await act.Should().NotThrowAsync()).Which;
        Flatten(view.Components!).Should().Contain(component => component.Component == Ui.EmptyStateComponent);
        logger.Levels.Should().Contain(LogLevel.Error);
    }

    [Fact]
    public async Task GetViewAsync_WhenSecretReadThrows_RendersErrorViewInsteadOfThrowing()
    {
        TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        RecordingLogger logger = new();
        context.Logger = logger;
        context.Configuration.Stored = new TorrentDownloaderSettings { Indexers = [new IndexerSettings { Name = "Prowlarr" }] };
        context.Secrets.ThrowOnGet = new CryptographicException("key ring rotated");
        plugin.Initialize(context);

        Func<Task<PluginView>> act = () => plugin.GetViewAsync(new PluginViewRequest { Route = "/settings" }, CancellationToken.None);

        PluginView view = (await act.Should().NotThrowAsync()).Which;
        Flatten(view.Components!).Should().Contain(component => component.Component == Ui.EmptyStateComponent);
        logger.Levels.Should().Contain(LogLevel.Error);
    }

    [Fact]
    public async Task GetViewAsync_RoutesDownloadsToThePageThatSaysWhatIsDownloading()
    {
        TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        await SeedStoreAsync(context);
        plugin.Initialize(context);

        PluginView view = await plugin.GetViewAsync(new PluginViewRequest { Route = "/downloads" }, CancellationToken.None);

        List<string> words = [.. PluginNodes.Words(view)];
        words.Should().Contain("Some.Show.S01E01.1080p", "the active list names the release being downloaded");
        words.Should().Contain("50%", "500 of 1000 bytes is half of it");
        words.Should().Contain("Some Show", "the queue names the show an episode is still missing from");
    }

    [Fact]
    public async Task GetViewAsync_DownloadsPageReadsTheStoreWithoutStartingTheEngine()
    {
        TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();

        // A configured indexer host is what makes this observable: building the pipeline
        // asks the host for access to it, and that request is recorded. The page reads
        // settings of its own now - which shows the owner follows - so a settings read is
        // no longer the signal. This is.
        context.Configuration.Stored = new TorrentDownloaderSettings
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.test", Enabled = true }],
        };
        await SeedStoreAsync(context);
        plugin.Initialize(context);

        await plugin.GetViewAsync(new PluginViewRequest { Route = "/downloads" }, CancellationToken.None);

        // Opening a page must not start a BitTorrent engine: someone browsing the
        // dashboard would be dialling peers without a tick having run.
        context.Grants.Requests.Should().BeEmpty("only building the pipeline asks for host access");
        context.HttpHandler.Requests.Should().BeEmpty();
    }

    // The test that would have caught a page built, tested, and then reachable from
    // nowhere: a mount the plugin advertises and does not answer renders as "Nothing here".
    [Fact]
    public async Task GetViewAsync_AnswersEveryRouteTheNavEntriesAdvertise()
    {
        TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        context.Configuration.Stored = new TorrentDownloaderSettings();
        plugin.Initialize(context);

        foreach (PluginNavEntry entry in plugin.NavEntries)
        {
            PluginView view = await plugin.GetViewAsync(new PluginViewRequest { Route = entry.Route }, CancellationToken.None);

            PluginNodes.All(view).Should().NotContain(
                node => node.Id == "unknown-route",
                $"the plugin mounts {entry.Route}, so it has to answer it");
        }
    }

    [Fact]
    public async Task GetViewAsync_RouteThisVersionDoesNotHaveSaysSoInsteadOfFailing()
    {
        TorrentDownloaderPlugin plugin = new();
        plugin.Initialize(new FakePluginContext());

        PluginView view = await plugin.GetViewAsync(new PluginViewRequest { Route = "/history" }, CancellationToken.None);

        PluginNodes.All(view).Should().Contain(node => node.Id == "unknown-route");
    }

    [Fact]
    public void Identity_MatchesPluginIdentity()
    {
        TorrentDownloaderPlugin plugin = new();

        plugin.Name.Should().Be(PluginIdentity.Name);
        plugin.Description.Should().Be(PluginIdentity.Description);
        plugin.Id.Should().Be(PluginIdentity.Id);
        plugin.Version.Should().Be(PluginIdentity.Version);
    }

    [Fact]
    public void Jobs_DeclaresTheFourCadences()
    {
        TorrentDownloaderPlugin plugin = new();
        plugin.Initialize(new FakePluginContext());

        IReadOnlyList<PluginScheduledJob> jobs = plugin.Jobs;

        jobs.Should().HaveCount(4);
        jobs.Select(job => job.Name)
            .Should()
            .BeEquivalentTo([JobNames.Transfers, JobNames.Feed, JobNames.Search, JobNames.Maintenance]);
    }

    [Fact]
    public void Jobs_TakesEachCronFromSettings()
    {
        TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        context.Configuration.Stored = new TorrentDownloaderSettings
        {
            TransfersCron = "*/2 * * * *",
            FeedCron = "*/20 * * * *",
            SearchCron = "0 */3 * * *",
            MaintenanceCron = "0 5 * * *",
        };
        plugin.Initialize(context);

        IReadOnlyList<PluginScheduledJob> jobs = plugin.Jobs;

        jobs.Single(job => job.Name == JobNames.Transfers).CronExpression.Should().Be("*/2 * * * *");
        jobs.Single(job => job.Name == JobNames.Feed).CronExpression.Should().Be("*/20 * * * *");
        jobs.Single(job => job.Name == JobNames.Search).CronExpression.Should().Be("0 */3 * * *");
        jobs.Single(job => job.Name == JobNames.Maintenance).CronExpression.Should().Be("0 5 * * *");
    }

    [Fact]
    public void Jobs_WhenConfigurationReadThrows_ReturnsDefaultJobsAndDoesNotThrow()
    {
        TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        RecordingLogger logger = new();
        context.Logger = logger;
        context.Configuration.ThrowOnRead = new JsonException("truncated settings file");
        plugin.Initialize(context);

        TorrentDownloaderSettings defaults = new();
        Func<IReadOnlyList<PluginScheduledJob>> act = () => plugin.Jobs;

        IReadOnlyList<PluginScheduledJob> jobs = act.Should().NotThrow().Which;
        jobs.Single(job => job.Name == JobNames.Transfers).CronExpression.Should().Be(defaults.TransfersCron);
        jobs.Single(job => job.Name == JobNames.Feed).CronExpression.Should().Be(defaults.FeedCron);
        jobs.Single(job => job.Name == JobNames.Search).CronExpression.Should().Be(defaults.SearchCron);
        jobs.Single(job => job.Name == JobNames.Maintenance).CronExpression.Should().Be(defaults.MaintenanceCron);
        logger.Levels.Should().Contain(LogLevel.Warning);
    }

    [Fact]
    public void Jobs_WhenTransfersCronIsNull_UsesDefaultForThatFieldOnly()
    {
        TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        context.Configuration.Stored = new TorrentDownloaderSettings
        {
            TransfersCron = null!,
            FeedCron = "*/20 * * * *",
        };
        plugin.Initialize(context);

        IReadOnlyList<PluginScheduledJob> jobs = plugin.Jobs;

        jobs.Single(job => job.Name == JobNames.Transfers).CronExpression.Should().Be(new TorrentDownloaderSettings().TransfersCron);
        jobs.Single(job => job.Name == JobNames.Feed).CronExpression.Should().Be("*/20 * * * *");
    }

    [Fact]
    public void Jobs_WhenSearchCronIsWhitespace_UsesDefaultForThatFieldOnly()
    {
        TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        context.Configuration.Stored = new TorrentDownloaderSettings
        {
            SearchCron = "   ",
            MaintenanceCron = "0 5 * * *",
        };
        plugin.Initialize(context);

        IReadOnlyList<PluginScheduledJob> jobs = plugin.Jobs;

        jobs.Single(job => job.Name == JobNames.Search).CronExpression.Should().Be(new TorrentDownloaderSettings().SearchCron);
        jobs.Single(job => job.Name == JobNames.Maintenance).CronExpression.Should().Be("0 5 * * *");
    }

    [Fact]
    public void Jobs_DisallowConcurrentExecution()
    {
        TorrentDownloaderPlugin plugin = new();
        plugin.Initialize(new FakePluginContext());

        IReadOnlyList<PluginScheduledJob> jobs = plugin.Jobs;

        jobs.Should().OnlyContain(job => job.AllowConcurrent == false);
    }

    [Fact]
    public void CronExpression_MatchesTheTransfersJob()
    {
        TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        context.Configuration.Stored = new TorrentDownloaderSettings { TransfersCron = "*/7 * * * *" };
        plugin.Initialize(context);

        string transfersJobCron = plugin.Jobs.Single(job => job.Name == JobNames.Transfers).CronExpression;

        plugin.CronExpression.Should().Be(transfersJobCron);
        plugin.CronExpression.Should().Be("*/7 * * * *");
    }

    [Fact]
    public async Task ExecuteAsync_RoutesEachJobNameToItsOwnWork()
    {
        TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        RecordingLogger logger = new();
        context.Logger = logger;
        plugin.Initialize(context);

        await plugin.ExecuteAsync(JobNames.Transfers, CancellationToken.None);
        await plugin.ExecuteAsync(JobNames.Feed, CancellationToken.None);
        await plugin.ExecuteAsync(JobNames.Search, CancellationToken.None);
        await plugin.ExecuteAsync(JobNames.Maintenance, CancellationToken.None);

        // Transfers and Search now say nothing when there is nothing to report - a cycle
        // that found no finished downloads and started none should not log every minute.
        // The two that always report do, and they report different things, which is what
        // proves the switch is not funnelling every job into one body.
        logger.Messages.Should().HaveCount(2);
        logger.Messages.Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsForAnUnknownJobName()
    {
        TorrentDownloaderPlugin plugin = new();
        plugin.Initialize(new FakePluginContext());

        Func<Task> act = () => plugin.ExecuteAsync("not-a-real-job", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ExecuteAsync_BeforeInitializeThrowsInvalidOperation()
    {
        TorrentDownloaderPlugin plugin = new();

        Func<Task> act = () => plugin.ExecuteAsync(JobNames.Transfers, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ExecuteAsync_HonoursCancellation()
    {
        TorrentDownloaderPlugin plugin = new();
        plugin.Initialize(new FakePluginContext());
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        Func<Task> act = () => plugin.ExecuteAsync(JobNames.Transfers, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

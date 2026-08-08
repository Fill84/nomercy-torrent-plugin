// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

public class PluginLifecycleTests
{
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
        Flatten(view.Components!).Should().Contain(component => component.Component == PluginComponentType.EmptyState);
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
        Flatten(view.Components!).Should().Contain(component => component.Component == PluginComponentType.EmptyState);
        logger.Levels.Should().Contain(LogLevel.Error);
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

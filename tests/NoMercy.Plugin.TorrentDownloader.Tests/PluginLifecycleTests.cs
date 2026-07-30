// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

public class PluginLifecycleTests
{
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

        // Four ticks, four distinct log messages: proof each job name reached its own body
        // rather than all four quietly funnelling into the same work.
        logger.Messages.Should().HaveCount(4);
        logger.Messages.Distinct().Should().HaveCount(4);
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

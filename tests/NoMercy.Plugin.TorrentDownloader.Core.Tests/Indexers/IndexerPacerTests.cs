// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Indexers;

public class IndexerPacerTests
{
    private static IndexerPacer Pacer(FakeClock clock) =>
        new(clock, TimeSpan.FromSeconds(2), maxConcurrency: 2, failureThreshold: 3, cooldown: TimeSpan.FromMinutes(5));

    [Fact]
    public async Task RunAsync_DoesNotDelayTheFirstCall()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);

        await Pacer(clock).RunAsync(_ => Task.FromResult(1), CancellationToken.None);

        clock.Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_WaitsTheMinimumIntervalBetweenCalls()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = Pacer(clock);

        await pacer.RunAsync(_ => Task.FromResult(1), CancellationToken.None);
        await pacer.RunAsync(_ => Task.FromResult(2), CancellationToken.None);

        clock.Delays.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RunAsync_DoesNotWaitWhenEnoughTimeAlreadyPassed()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = Pacer(clock);

        await pacer.RunAsync(_ => Task.FromResult(1), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(30));
        await pacer.RunAsync(_ => Task.FromResult(2), CancellationToken.None);

        clock.Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_BacksOffExponentiallyOnRateLimitResponses()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = new(clock, TimeSpan.Zero, 2, failureThreshold: 99, cooldown: TimeSpan.FromMinutes(5));

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Func<Task> act = () =>
                pacer.RunAsync<int>(
                    _ => throw new IndexerException("x: search returned HTTP 429"),
                    CancellationToken.None
                );
            await act.Should().ThrowAsync<IndexerException>();
        }

        clock.Delays.Should().HaveCountGreaterThan(1);
        clock.Delays[^1].Should().BeGreaterThan(clock.Delays[0]);
    }

    [Fact]
    public async Task RunAsync_ParksTheIndexerAfterConsecutiveFailures()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = Pacer(clock);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Func<Task> act = () =>
                pacer.RunAsync<int>(_ => throw new IndexerException("boom"), CancellationToken.None);
            await act.Should().ThrowAsync<IndexerException>();
        }

        pacer.IsParked.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_ThrowsWithoutCallingTheWorkWhileParked()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = Pacer(clock);
        bool called = false;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Func<Task> fail = () =>
                pacer.RunAsync<int>(_ => throw new IndexerException("boom"), CancellationToken.None);
            await fail.Should().ThrowAsync<IndexerException>();
        }

        Func<Task> act = () =>
            pacer.RunAsync<int>(
                _ =>
                {
                    called = true;
                    return Task.FromResult(1);
                },
                CancellationToken.None
            );

        (await act.Should().ThrowAsync<IndexerException>()).And.Message.Should().Contain("parked");
        called.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_UnparksAfterTheCooldownElapses()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = Pacer(clock);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Func<Task> fail = () =>
                pacer.RunAsync<int>(_ => throw new IndexerException("boom"), CancellationToken.None);
            await fail.Should().ThrowAsync<IndexerException>();
        }

        clock.Advance(TimeSpan.FromMinutes(6));

        int result = await pacer.RunAsync(_ => Task.FromResult(7), CancellationToken.None);

        result.Should().Be(7);
        pacer.IsParked.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_ResetsTheFailureCountOnSuccess()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = Pacer(clock);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            Func<Task> fail = () =>
                pacer.RunAsync<int>(_ => throw new IndexerException("boom"), CancellationToken.None);
            await fail.Should().ThrowAsync<IndexerException>();
        }

        await pacer.RunAsync(_ => Task.FromResult(1), CancellationToken.None);

        Func<Task> once = () =>
            pacer.RunAsync<int>(_ => throw new IndexerException("boom"), CancellationToken.None);
        await once.Should().ThrowAsync<IndexerException>();

        pacer.IsParked.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_NeverRunsMoreThanTheConcurrencyCapAtOnce()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = new(clock, TimeSpan.Zero, maxConcurrency: 2, failureThreshold: 99, cooldown: TimeSpan.FromMinutes(5));
        int running = 0;
        int peak = 0;

        async Task<int> Work(CancellationToken ct)
        {
            int now = Interlocked.Increment(ref running);
            peak = Math.Max(peak, now);
            await Task.Yield();
            Interlocked.Decrement(ref running);
            return now;
        }

        await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => pacer.RunAsync(Work, CancellationToken.None))
        );

        peak.Should().BeLessThanOrEqualTo(2);
    }

    // Pins a guarantee this class relies on rather than provides: SemaphoreSlim's own constructor
    // rejects a non-positive maxCount, so no explicit guard is needed here. The test exists so that
    // clamping the cap instead of passing it through would be caught.
    [Fact]
    public void Constructor_RejectsAConcurrencyCapOfZero()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);

        Action act = () =>
            _ = new IndexerPacer(clock, TimeSpan.Zero, 0, 3, TimeSpan.FromMinutes(5));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

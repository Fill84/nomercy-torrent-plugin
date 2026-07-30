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

    // The backoff is no longer awaited (Fix 4), so it no longer shows up as growing entries in
    // clock.Delays; it is now observable only through the "backing off" gate on the next call.
    // Classification also moved from message substring-matching to IndexerException.StatusCode
    // (Fix 5), so the thrown exceptions must carry a real status code.
    [Fact]
    public async Task RunAsync_BacksOffExponentiallyOnRateLimitResponses()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = new(clock, TimeSpan.Zero, 2, failureThreshold: 99, cooldown: TimeSpan.FromMinutes(5));
        List<string> backoffMessages = [];

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Func<Task> work = () =>
                pacer.RunAsync<int>(
                    _ => throw new IndexerException("x: search returned HTTP 429", 429),
                    CancellationToken.None
                );
            await work.Should().ThrowAsync<IndexerException>();

            Func<Task> gated = () => pacer.RunAsync<int>(_ => Task.FromResult(1), CancellationToken.None);
            IndexerException blocked = (await gated.Should().ThrowAsync<IndexerException>()).Which;
            blocked.Message.Should().Contain("backing off");
            backoffMessages.Add(blocked.Message);

            clock.Advance(TimeSpan.FromMinutes(3));
        }

        backoffMessages[0].Should().Contain("2s");
        backoffMessages[1].Should().Contain("4s");
        backoffMessages[2].Should().Contain("8s");
    }

    [Fact]
    public async Task RunAsync_RateLimitedFailureDoesNotAwaitTheBackoff()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = new(clock, TimeSpan.Zero, 2, failureThreshold: 99, cooldown: TimeSpan.FromMinutes(5));

        Func<Task> act = () =>
            pacer.RunAsync<int>(
                _ => throw new IndexerException("x: search returned HTTP 429", 429),
                CancellationToken.None
            );
        await act.Should().ThrowAsync<IndexerException>();

        clock.Delays.Should().BeEmpty();

        Func<Task> next = () => pacer.RunAsync<int>(_ => Task.FromResult(1), CancellationToken.None);
        (await next.Should().ThrowAsync<IndexerException>()).Which.Message.Should().Contain("backing off");
    }

    [Fact]
    public async Task RunAsync_ClassifiesRateLimitsByStatusCodeNotMessageText()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = new(clock, TimeSpan.Zero, 2, failureThreshold: 99, cooldown: TimeSpan.FromMinutes(5));

        Func<Task> act = () =>
            pacer.RunAsync<int>(
                _ => throw new IndexerException(
                    "x: feed failed: Unexpected end of file. Line 503, position 15."
                ),
                CancellationToken.None
            );
        await act.Should().ThrowAsync<IndexerException>();

        clock.Delays.Should().BeEmpty();

        int result = await pacer.RunAsync(_ => Task.FromResult(42), CancellationToken.None);
        result.Should().Be(42);
    }

    [Fact]
    public async Task RunAsync_ClampsTheBackoffAtMaxBackoff()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = new(clock, TimeSpan.Zero, 2, failureThreshold: 1000, cooldown: TimeSpan.FromMinutes(5));

        for (int attempt = 0; attempt < 10; attempt++)
        {
            clock.Advance(TimeSpan.FromMinutes(3));
            Func<Task> act = () =>
                pacer.RunAsync<int>(
                    _ => throw new IndexerException("x: search returned HTTP 429", 429),
                    CancellationToken.None
                );
            await act.Should().ThrowAsync<IndexerException>();
        }

        Func<Task> gated = () => pacer.RunAsync<int>(_ => Task.FromResult(1), CancellationToken.None);
        IndexerException blocked = (await gated.Should().ThrowAsync<IndexerException>()).Which;

        double seconds = double.Parse(blocked.Message.Split("another ")[1].Split("s after")[0]);
        seconds.Should().BeLessThanOrEqualTo(120);
    }

    // The measured overflow: BaseBackoff * Math.Pow(2, hits - 1) overflows TimeSpan around hits=40
    // when the exponent is not clamped before the multiplication. failureThreshold is set high
    // enough that the park gate never trips, since a park rejection does not go through OnFailure
    // and would stop rateLimitHits from growing. The clock is advanced past MaxBackoff (2 minutes)
    // before every attempt so each call actually reaches the failing work instead of being turned
    // away by its own backing-off gate — that is what lets 45 *rate-limited* hits accumulate rather
    // than 45 gate rejections.
    [Fact]
    public async Task RunAsync_DoesNotOverflowAfter45ConsecutiveRateLimitedFailures()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = new(clock, TimeSpan.Zero, 2, failureThreshold: 1000, cooldown: TimeSpan.FromMinutes(5));

        for (int attempt = 0; attempt < 45; attempt++)
        {
            clock.Advance(TimeSpan.FromMinutes(3));
            Func<Task> act = () =>
                pacer.RunAsync<int>(
                    _ => throw new IndexerException("x: search returned HTTP 429", 429),
                    CancellationToken.None
                );
            (await act.Should().ThrowAsync<IndexerException>()).Which.Message.Should()
                .Be("x: search returned HTTP 429");
        }
    }

    [Fact]
    public async Task RunAsync_ANonIndexerExceptionFromWorkStillCountsTowardThePark()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = Pacer(clock);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Func<Task> act = () =>
                pacer.RunAsync<int>(
                    _ => throw new InvalidOperationException("boom"),
                    CancellationToken.None
                );
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        pacer.IsParked.Should().BeTrue();
    }

    // Cancellation has to happen while the work delegate is running, not before the call. A token
    // that is already cancelled on entry makes _slots.WaitAsync throw ahead of the try, so the
    // catch filter this test is about would never be reached and the test would pass no matter what
    // that filter said.
    [Fact]
    public async Task RunAsync_CallerCancellationDoesNotCountTowardThePark()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = Pacer(clock);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            using CancellationTokenSource source = new();
            Func<Task> act = () =>
                pacer.RunAsync<int>(
                    async token =>
                    {
                        await source.CancelAsync();
                        token.ThrowIfCancellationRequested();
                        return 0;
                    },
                    source.Token
                );
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        pacer.IsParked.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_AddsTheBackoffOnTopOfTheMinimumIntervalRatherThanAbsorbingIt()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerPacer pacer = new(
            clock,
            TimeSpan.FromSeconds(15),
            maxConcurrency: 2,
            failureThreshold: 99,
            cooldown: TimeSpan.FromMinutes(5)
        );

        Func<Task> rateLimited = () =>
            pacer.RunAsync<int>(
                _ => throw new IndexerException("x: search returned HTTP 429", 429),
                CancellationToken.None
            );
        await rateLimited.Should().ThrowAsync<IndexerException>();

        // A 2s backoff shorter than the 15s interval must still buy spacing. Comparing the two
        // instead of adding them would leave the indexer free again at 15s.
        clock.Advance(TimeSpan.FromSeconds(15));
        Func<Task> tooSoon = () => pacer.RunAsync(_ => Task.FromResult(1), CancellationToken.None);
        (await tooSoon.Should().ThrowAsync<IndexerException>()).Which.Message.Should()
            .Contain("backing off");

        clock.Advance(TimeSpan.FromSeconds(3));
        int result = await pacer.RunAsync(_ => Task.FromResult(42), CancellationToken.None);
        result.Should().Be(42);
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

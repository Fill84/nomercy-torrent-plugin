// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

public sealed class IndexerPacer(
    IClock clock,
    TimeSpan minimumInterval,
    int maxConcurrency,
    int failureThreshold,
    TimeSpan cooldown
) : IDisposable
{
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);

    private const int MaxBackoffExponent = 20;

    private readonly SemaphoreSlim _slots = new(maxConcurrency, maxConcurrency);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _state = new();

    private DateTimeOffset _lastStarted = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private int _rateLimitHits;
    private DateTimeOffset? _parkedUntil;
    private DateTimeOffset? _backoffUntil;

    public bool IsParked => RemainingPark() is not null;

    public TimeSpan? ParkRemaining => RemainingPark();

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        if (RemainingPark() is TimeSpan remaining)
            throw new IndexerException(
                $"indexer is parked for another {remaining.TotalSeconds:F0}s after repeated failures"
            );

        if (RemainingBackoff() is TimeSpan backingOff)
            throw new IndexerException(
                $"indexer is backing off for another {backingOff.TotalSeconds:F0}s after a rate limit"
            );

        await _slots.WaitAsync(ct);

        try
        {
            await WaitForIntervalAsync(ct);
            T result = await work(ct);
            OnSuccess();
            return result;
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            OnFailure(error);
            throw;
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task WaitForIntervalAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);

        try
        {
            TimeSpan since = clock.UtcNow - _lastStarted;
            if (since < minimumInterval)
                await clock.DelayAsync(minimumInterval - since, ct);

            _lastStarted = clock.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }

    private TimeSpan? RemainingPark()
    {
        lock (_state)
        {
            return _parkedUntil is DateTimeOffset until && clock.UtcNow < until
                ? until - clock.UtcNow
                : null;
        }
    }

    private TimeSpan? RemainingBackoff()
    {
        lock (_state)
        {
            return _backoffUntil is DateTimeOffset until && clock.UtcNow < until
                ? until - clock.UtcNow
                : null;
        }
    }

    private void OnSuccess()
    {
        lock (_state)
        {
            _consecutiveFailures = 0;
            _rateLimitHits = 0;
            _parkedUntil = null;
            _backoffUntil = null;
        }
    }

    private void OnFailure(Exception error)
    {
        lock (_state)
        {
            _consecutiveFailures++;

            if (IsRateLimited(error))
            {
                _rateLimitHits++;
                int exponent = Math.Min(_rateLimitHits - 1, MaxBackoffExponent);
                TimeSpan computed = BaseBackoff * Math.Pow(2, exponent);
                TimeSpan backoff = computed < MaxBackoff ? computed : MaxBackoff;

                // The interval is added rather than compared against. Both windows would otherwise
                // start at this same failure, so the backoff would be absorbed whenever it was
                // shorter than the interval and the indexer would see no extra spacing at all —
                // which is the whole point of backing off after it asked us to slow down.
                _backoffUntil = clock.UtcNow + minimumInterval + backoff;
            }

            if (_consecutiveFailures >= failureThreshold)
                _parkedUntil = clock.UtcNow + cooldown;
        }
    }

    private static bool IsRateLimited(Exception error) =>
        error is IndexerException { StatusCode: 429 or 503 or 509 };

    public void Dispose()
    {
        _slots.Dispose();
        _gate.Dispose();
    }
}

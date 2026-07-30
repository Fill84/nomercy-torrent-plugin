// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
//
// NoMercy MediaServer Automated Torrent Plugin
// Created by Phillippe Pelzer https://github.com/Fill84
// -----------------------------------------------------------------------------

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

    private readonly SemaphoreSlim _slots = new(maxConcurrency, maxConcurrency);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _state = new();

    private DateTimeOffset _lastStarted = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private int _rateLimitHits;
    private DateTimeOffset? _parkedUntil;

    public bool IsParked => RemainingPark() is not null;

    public TimeSpan? ParkedUntil => RemainingPark();

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        if (RemainingPark() is TimeSpan remaining)
            throw new IndexerException(
                $"indexer is parked for another {remaining.TotalSeconds:F0}s after repeated failures"
            );

        await _slots.WaitAsync(ct);

        try
        {
            await WaitForIntervalAsync(ct);
            T result = await work(ct);
            OnSuccess();
            return result;
        }
        catch (IndexerException error)
        {
            await OnFailureAsync(error, ct);
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

    private void OnSuccess()
    {
        lock (_state)
        {
            _consecutiveFailures = 0;
            _rateLimitHits = 0;
            _parkedUntil = null;
        }
    }

    private async Task OnFailureAsync(IndexerException error, CancellationToken ct)
    {
        bool rateLimited = IsRateLimited(error);
        TimeSpan? backoff = null;
        int consecutiveFailures;

        lock (_state)
        {
            _consecutiveFailures++;
            consecutiveFailures = _consecutiveFailures;

            if (rateLimited)
            {
                _rateLimitHits++;
                TimeSpan computed = BaseBackoff * Math.Pow(2, _rateLimitHits - 1);
                backoff = computed < MaxBackoff ? computed : MaxBackoff;
            }
        }

        if (backoff is TimeSpan delay)
            await clock.DelayAsync(delay, ct);

        if (consecutiveFailures >= failureThreshold)
        {
            lock (_state)
            {
                _parkedUntil = clock.UtcNow + cooldown;
            }
        }
    }

    private static bool IsRateLimited(IndexerException error) =>
        error.Message.Contains("429", StringComparison.Ordinal)
        || error.Message.Contains("503", StringComparison.Ordinal);

    public void Dispose()
    {
        _slots.Dispose();
        _gate.Dispose();
    }
}

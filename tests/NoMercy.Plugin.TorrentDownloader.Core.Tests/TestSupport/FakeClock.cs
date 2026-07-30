// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Indexers;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

public sealed class FakeClock(DateTimeOffset start) : IClock
{
    private DateTimeOffset _now = start;

    public DateTimeOffset UtcNow => _now;

    public List<TimeSpan> Delays { get; } = [];

    public void Advance(TimeSpan duration) => _now = _now.Add(duration);

    public Task DelayAsync(TimeSpan duration, CancellationToken ct)
    {
        Delays.Add(duration);
        _now = _now.Add(duration);
        return Task.CompletedTask;
    }
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Indexers;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

// A settable double for IClock, mirroring Core.Tests' own FakeClock: that one lives in a
// project this project cannot reference (test projects are not exposed to one another), so
// SettingsSaveHandlerTests needs its own instance of the same shape rather than a shared one.
public sealed class FakeClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset UtcNow { get; } = start;

    public Task DelayAsync(TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
}

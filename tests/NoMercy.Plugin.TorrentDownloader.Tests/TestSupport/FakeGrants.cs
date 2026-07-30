// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

// A settable double for IPluginGrants over a set of (kind, value) pairs. Requests are
// recorded rather than acted on, matching the real implementation's contract: RequestAsync
// records and returns immediately, it never waits for a human.
public sealed class FakeGrants : IPluginGrants
{
    private readonly HashSet<(string Kind, string Value)> _granted = [];

    public List<(string Kind, string Value, string Reason)> Requests { get; } = [];

    public void Grant(string kind, string value)
    {
        _granted.Add((kind, value));
    }

    public Task<bool> HasAsync(string kind, string value, CancellationToken ct = default)
    {
        return Task.FromResult(_granted.Contains((kind, value)));
    }

    public Task<IReadOnlyList<string>> GetAsync(string kind, CancellationToken ct = default)
    {
        IReadOnlyList<string> values = [.. _granted.Where(entry => entry.Kind == kind).Select(entry => entry.Value)];
        return Task.FromResult(values);
    }

    public Task RequestAsync(string kind, string value, string reason, CancellationToken ct = default)
    {
        Requests.Add((kind, value, reason));
        return Task.CompletedTask;
    }
}

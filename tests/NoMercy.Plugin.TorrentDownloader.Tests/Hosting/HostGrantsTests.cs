// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

public class HostGrantsTests
{
    [Fact]
    public async Task EnsureAsync_RequestsAHostThatIsNotGranted()
    {
        FakeGrants grants = new();
        HostGrants hostGrants = new(grants);
        TorrentDownloaderSettings settings = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" }],
        };

        await hostGrants.EnsureAsync(settings, CancellationToken.None);

        grants.Requests.Should().ContainSingle(request => request.Kind == PluginGrantKind.NetworkHost && request.Value == "prowlarr.local");
    }

    [Fact]
    public async Task EnsureAsync_DoesNotRequestAHostAlreadyGranted()
    {
        FakeGrants grants = new();
        grants.Grant(PluginGrantKind.NetworkHost, "prowlarr.local");
        HostGrants hostGrants = new(grants);
        TorrentDownloaderSettings settings = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" }],
        };

        await hostGrants.EnsureAsync(settings, CancellationToken.None);

        grants.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureAsync_ReturnsUngrantedHostsSoTheyCanBeShown()
    {
        FakeGrants grants = new();
        HostGrants hostGrants = new(grants);
        TorrentDownloaderSettings settings = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" }],
        };

        IReadOnlyList<string> ungranted = await hostGrants.EnsureAsync(settings, CancellationToken.None);

        ungranted.Should().ContainSingle().Which.Should().Be("prowlarr.local");
    }

    [Fact]
    public async Task EnsureAsync_ReturnsEmptyWhenEveryHostIsGranted()
    {
        FakeGrants grants = new();
        grants.Grant(PluginGrantKind.NetworkHost, "prowlarr.local");
        HostGrants hostGrants = new(grants);
        TorrentDownloaderSettings settings = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" }],
        };

        IReadOnlyList<string> ungranted = await hostGrants.EnsureAsync(settings, CancellationToken.None);

        ungranted.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureAsync_ExtractsTheHostFromAConfiguredUrl()
    {
        FakeGrants grants = new();
        HostGrants hostGrants = new(grants);
        TorrentDownloaderSettings settings = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local:9696/api" }],
        };

        IReadOnlyList<string> ungranted = await hostGrants.EnsureAsync(settings, CancellationToken.None);

        ungranted.Should().ContainSingle().Which.Should().Be("prowlarr.local");
        grants.Requests.Should().ContainSingle(request => request.Value == "prowlarr.local");
    }

    [Fact]
    public async Task EnsureAsync_IgnoresDisabledEntries()
    {
        FakeGrants grants = new();
        HostGrants hostGrants = new(grants);
        TorrentDownloaderSettings settings = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local", Enabled = false }],
        };

        IReadOnlyList<string> ungranted = await hostGrants.EnsureAsync(settings, CancellationToken.None);

        ungranted.Should().BeEmpty();
        grants.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureAsync_SkipsAnEntryWithAnUnparseableUrl()
    {
        FakeGrants grants = new();
        HostGrants hostGrants = new(grants);
        TorrentDownloaderSettings settings = new()
        {
            Indexers =
            [
                new IndexerSettings { Name = "EmptyUrl", Url = string.Empty },
                new IndexerSettings { Name = "MalformedUrl", Url = "not a url" },
                new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local" },
            ],
        };

        IReadOnlyList<string> ungranted = await hostGrants.EnsureAsync(settings, CancellationToken.None);

        ungranted.Should().ContainSingle().Which.Should().Be("prowlarr.local");
    }

    [Fact]
    public async Task EnsureAsync_RequestsEachDistinctHostOnce()
    {
        FakeGrants grants = new();
        HostGrants hostGrants = new(grants);
        TorrentDownloaderSettings settings = new()
        {
            Indexers =
            [
                new IndexerSettings { Name = "Prowlarr", Url = "https://prowlarr.local/api" },
                new IndexerSettings { Name = "Prowlarr2", Url = "https://prowlarr.local/api2" },
            ],
        };

        IReadOnlyList<string> ungranted = await hostGrants.EnsureAsync(settings, CancellationToken.None);

        ungranted.Should().ContainSingle().Which.Should().Be("prowlarr.local");
        grants.Requests.Should().ContainSingle(request => request.Value == "prowlarr.local");

        // The reason is shown to the owner, so it has to name the host being asked for and what it
        // is for. Asserted on those two facts rather than the whole sentence, so rewording the
        // prose does not fail the test but dropping either detail does.
        grants.Requests[0].Reason.Should().Contain("prowlarr.local").And.Contain("indexer");
    }

}

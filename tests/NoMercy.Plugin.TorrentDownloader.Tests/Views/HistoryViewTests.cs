// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>What became of each release, which used to be a section nothing asserted.</summary>
public class HistoryViewTests
{
    private static HistoryEntry Entry(
        HistoryEvent outcome,
        string title = "Some.Show.S01E01.1080p",
        string? detail = null,
        int minutesAgo = 5) => new()
    {
        At = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
        Event = outcome,
        Key = new EpisodeKey(1, 1, 1),
        ReleaseTitle = title,
        Detail = detail,
    };

    [Fact]
    public void Build_DrawsOnlyTagsAClientKnows()
    {
        PluginNodes.All(HistoryView.Build([Entry(HistoryEvent.Imported)])).Select(node => node.Component)
            .Should().OnlyContain(component => PluginNodes.KnownComponents.Contains(component!));
    }

    [Fact]
    public void Build_CarriesTheTabBar()
    {
        PluginNodes.All(HistoryView.Build([])).Should().Contain(node => node.Id == "tab-overview");
    }

    [Fact]
    public void Build_SaysNothingHasHappenedRatherThanShowingAnEmptyList()
    {
        PluginNodes.All(HistoryView.Build([])).Should().Contain(node => node.Id == "history-empty");
    }

    [Fact]
    public void Build_NamesTheOutcomeAndTheRelease()
    {
        PluginView view = HistoryView.Build([Entry(HistoryEvent.Imported, "Some.Show.S01E01.1080p")]);

        PluginNodes.Says(view, "Imported").Should().BeTrue();
        PluginNodes.Says(view, "Some.Show.S01E01.1080p").Should().BeTrue();
    }

    // "Failed" on its own sends a reader to the log file this page exists to save them from.
    [Fact]
    public void Build_SaysWhySomethingFailed()
    {
        PluginView view = HistoryView.Build([Entry(HistoryEvent.Failed, detail: "no peers answered")]);

        PluginNodes.Says(view, "no peers answered").Should().BeTrue();
    }

    // How long ago, rather than a timestamp: "5 min ago" is read at a glance, a date has to
    // be subtracted from now before it means anything.
    [Fact]
    public void Build_SaysHowLongAgoRatherThanWhen()
    {
        PluginView view = HistoryView.Build([Entry(HistoryEvent.Imported)]);

        PluginNodes.Says(view, "min ago").Should().BeTrue();
    }

    [Fact]
    public void Build_StopsAtTheLimitRatherThanRenderingAnArchive()
    {
        List<HistoryEntry> many =
        [
            .. Enumerable.Range(1, HistoryView.Limit + 20)
                .Select(number => Entry(HistoryEvent.Imported, $"Release.Number.{number}", minutesAgo: number)),
        ];

        PluginView view = HistoryView.Build(many);

        PluginNodes.All(view).Count(node => node.Id.StartsWith("history-row-", StringComparison.Ordinal))
            .Should().Be(HistoryView.Limit);
    }
}

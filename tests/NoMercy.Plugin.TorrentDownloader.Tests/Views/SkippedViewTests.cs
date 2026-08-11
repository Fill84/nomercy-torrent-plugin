// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>
/// What the plugin is refusing to pick. A release blacklisted for a fortnight is the most
/// likely reason an episode keeps not arriving, and an owner who cannot see the list has no
/// way to tell that from "nobody is seeding it".
/// </summary>
public class SkippedViewTests
{
    private static BlacklistEntry Skipped(string title, string reason = "failed to download", double? daysLeft = 14) => new()
    {
        ReleaseTitle = title,
        Reason = reason,
        AddedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        ExpiresAt = daysLeft is null ? null : DateTimeOffset.UtcNow.AddDays(daysLeft.Value),
    };

    [Fact]
    public void Build_DrawsOnlyTagsAClientKnows()
    {
        PluginNodes.All(SkippedView.Build([Skipped("Bad.Release")])).Select(node => node.Component)
            .Should().OnlyContain(component => PluginNodes.KnownComponents.Contains(component!));
    }

    [Fact]
    public void Build_CarriesTheTabBar()
    {
        PluginNodes.All(SkippedView.Build([])).Should().Contain(node => node.Id == "tab-history");
    }

    [Fact]
    public void Build_SaysNothingIsSkippedRatherThanShowingAnEmptyList()
    {
        PluginNodes.All(SkippedView.Build([])).Should().Contain(node => node.Id == "skipped-empty");
    }

    // Why it is skipped and how long for are the two things that decide whether the owner has
    // to do anything about it.
    [Fact]
    public void Build_SaysWhyAndForHowMuchLonger()
    {
        PluginView view = SkippedView.Build([Skipped("Bad.Release", "no peers answered")]);

        PluginNodes.Says(view, "Bad.Release").Should().BeTrue();
        PluginNodes.Says(view, "no peers answered").Should().BeTrue();
        PluginNodes.Says(view, "days left").Should().BeTrue();
    }

    [Fact]
    public void Build_SaysWhenSomethingIsSkippedForGood()
    {
        PluginNodes.Says(SkippedView.Build([Skipped("Bad.Release", daysLeft: null)]), "skipped for good")
            .Should().BeTrue();
    }

    [Fact]
    public void Build_OffersToAllowOneAgain()
    {
        BlacklistEntry entry = Skipped("Bad.Release");

        PluginComponent allow = PluginNodes.All(SkippedView.Build([entry]))
            .Should().ContainSingle(node => node.Id == $"skipped-allow-{entry.Handle}").Which;

        allow.Action!.Payload["method"].Should().Be($"AllowRelease/{entry.Handle}");
    }

    // An entry with no title still has to render as something. A blank row reads as a bug.
    [Fact]
    public void Build_FallsBackToTheHashWhenTheSourceNamedNothing()
    {
        BlacklistEntry unnamed = new()
        {
            InfoHash = "orphan-hash",
            Reason = "failed to download",
            AddedAt = DateTimeOffset.UtcNow,
        };

        PluginNodes.Says(SkippedView.Build([unnamed]), "orphan-hash").Should().BeTrue();
    }

    [Fact]
    public void Build_PutsTheMostRecentlySkippedFirst()
    {
        BlacklistEntry older = Skipped("Older.Release") with { AddedAt = DateTimeOffset.UtcNow.AddDays(-2) };
        BlacklistEntry newer = Skipped("Newer.Release") with { AddedAt = DateTimeOffset.UtcNow };

        PluginView view = SkippedView.Build([older, newer]);

        List<string> titles = [.. PluginNodes.Words(view).Where(word => word.EndsWith(".Release"))];

        titles.Should().Equal("Newer.Release", "Older.Release");
    }
}

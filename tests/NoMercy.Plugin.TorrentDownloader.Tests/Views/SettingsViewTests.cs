// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>
/// What is left on the settings page now that sources have their own: folders, schedules,
/// what quality to accept, and the private trackers.
/// </summary>
public class SettingsViewTests
{
    // A form's fields are read back through the tree the client actually renders, which is
    // the only version that sees what a viewer gets.
    private static List<PluginFormField> AllFormFields(PluginView view) => [.. PluginNodes.AllFields(view)];

    [Fact]
    public void Build_ReturnsADeclarativeTreeNotAWebView()
    {
        PluginView view = SettingsView.Build(new TorrentDownloaderSettings(), new HashSet<string>());

        view.Components.Should().NotBeNullOrEmpty();
        view.WebView.Should().BeNull();
    }

    // The page has to be leaveable. Its section of the sidebar draws nothing for this
    // plugin, so a page without the bar is a page whose only exit is the browser's back
    // button.
    [Fact]
    public void Build_CarriesTheTabBar()
    {
        PluginView view = SettingsView.Build(new TorrentDownloaderSettings(), new HashSet<string>());

        PluginNodes.All(view).Should().Contain(node => node.Id == "tab-queue");
    }

    // Sources moved to their own page. Leaving a second copy of the indexer forms here is
    // two pages writing the same config, and whichever one the owner used last wins.
    [Fact]
    public void Build_LeavesSourcesToTheSourcesPage()
    {
        TorrentDownloaderSettings settings = new() { Indexers = [new IndexerSettings { Name = "Prowlarr" }] };

        PluginView view = SettingsView.Build(settings, new HashSet<string>());

        PluginNodes.Forms(view).Should().NotContain(form => form.Id.Contains("indexer", StringComparison.Ordinal));
    }

    // --- private trackers ---------------------------------------------------------

    // The passkey in an announce URL is the account. Build is never handed the stored value,
    // so this asserts the field is shaped to receive one and never to show one - a rendered
    // passkey is a passkey in a browser cache, a screenshot and a support ticket, and it is
    // the same field the owner has to be able to replace.
    [Fact]
    public void Build_RendersAPrivateTrackersAnnounceUrlAsASecretThatIsNeverSentBack()
    {
        TorrentDownloaderSettings settings = new()
        {
            PrivateTrackers = [new PrivateTrackerSettings { Name = "RedFish" }],
        };
        HashSet<string> stored = new(StringComparer.Ordinal) { SettingsGateway.PrivateTrackerAnnounceKey("RedFish") };

        PluginView view = SettingsView.Build(settings, stored);

        PluginFormField field = AllFormFields(view).Should()
            .ContainSingle(field => PluginNodes.Name(field) == "announceUrl").Which;

        PluginNodes.Type(field).Should().Be(PluginFormFieldType.Password);
        PluginNodes.Value(field).Should().BeNull();
        PluginNodes.Placeholder(field).Should().Contain("already saved");
    }

    [Fact]
    public void Build_SaysAPrivateTrackerIsNotConfiguredRatherThanShowingNothing()
    {
        PluginView view = SettingsView.Build(new TorrentDownloaderSettings(), new HashSet<string>());

        // "Nothing here" reads as broken. The empty state has to say what the absence means,
        // and what it means is that nothing will ever be uploaded.
        PluginNodes.All(view).Should().Contain(component => component.Id == "settings-trackers-empty");
    }

    // A PluginForm's submit discards the intent's payload, so an entry's identity has to
    // ride in the method string or the wrong tracker is edited.
    [Fact]
    public void Build_PrivateTrackerFormsActionEncodesTheEntrysRenderIndexInTheMethod()
    {
        TorrentDownloaderSettings settings = new()
        {
            PrivateTrackers = [new PrivateTrackerSettings { Name = "RedFish" }, new PrivateTrackerSettings { Name = "BlueFish" }],
        };

        PluginView view = SettingsView.Build(settings, new HashSet<string>());

        PluginComponent second = PluginNodes.All(view).Single(component => component.Id == "settings-tracker-1-form");
        second.Action!.Payload["method"].Should().Be("SavePrivateTracker/1");
    }

    // Specials are off by default and the owner has to be able to see that, and change it. A
    // default that can only be changed by editing config.json is not a setting.
    [Fact]
    public void Build_OffersTheSpecialsToggleShowingWhatIsCurrentlySet()
    {
        PluginView view = SettingsView.Build(new TorrentDownloaderSettings(), new HashSet<string>());

        PluginFormField field = AllFormFields(view).Should()
            .ContainSingle(field => PluginNodes.Name(field) == "includeSpecials").Which;

        PluginNodes.Type(field).Should().Be(PluginFormFieldType.Toggle);
        PluginNodes.Checked(field).Should().BeFalse();
    }

    [Fact]
    public void Build_ShowsTheSpecialsToggleOnWhenItIsOn()
    {
        PluginView view = SettingsView.Build(
            new TorrentDownloaderSettings { IncludeSpecials = true },
            new HashSet<string>());

        PluginFormField field = AllFormFields(view).Single(field => PluginNodes.Name(field) == "includeSpecials");

        PluginNodes.Checked(field).Should().BeTrue();
    }

    // The general form's action carries a method the client can resolve without help: no
    // identifying field, no payload of its own.
    [Fact]
    public void Build_GeneralFormsActionIsPlainSaveSettingsWithNoPayload()
    {
        PluginView view = SettingsView.Build(new TorrentDownloaderSettings(), new HashSet<string>());

        PluginComponent form = PluginNodes.All(view).Should()
            .ContainSingle(component => component.Id == "settings-general-form").Which;

        form.Action!.Payload["method"].Should().Be("SaveSettings");
        form.Action.Payload["payload"].Should().BeNull();
    }

    [Fact]
    public void Build_RendersNotSavedYetWhenNoTimestampIsRecorded()
    {
        PluginView view = SettingsView.Build(new TorrentDownloaderSettings(), new HashSet<string>());

        PluginNodes.Says(view, "Not saved yet").Should().BeTrue();
    }

    [Fact]
    public void Build_RendersTheSavedTimestampInInvariantCultureWhenSet()
    {
        TorrentDownloaderSettings settings = new() { LastSavedAtUtc = new DateTimeOffset(2026, 7, 31, 1, 59, 0, TimeSpan.Zero) };

        PluginView view = SettingsView.Build(settings, new HashSet<string>());

        PluginNodes.Says(view, "2026-07-31 01:59").Should().BeTrue();
    }

    [Fact]
    public void Build_DrawsOnlyTagsAClientKnows()
    {
        PluginView view = SettingsView.Build(
            new TorrentDownloaderSettings { PrivateTrackers = [new PrivateTrackerSettings { Name = "RedFish" }] },
            new HashSet<string>());

        PluginNodes.All(view).Select(node => node.Component)
            .Should().OnlyContain(component => PluginNodes.KnownComponents.Contains(component!));
    }
}

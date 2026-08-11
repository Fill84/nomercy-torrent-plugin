// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>
/// Where releases come from. Most of these came off the settings page with the indexers
/// themselves; the yield line is new, and is the reason the page exists separately.
/// </summary>
public class SourcesViewTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static PluginView Build(
        TorrentDownloaderSettings settings,
        IReadOnlyList<string>? ungranted = null,
        IReadOnlySet<string>? stored = null,
        IReadOnlyList<HistoryEntry>? history = null) =>
        SourcesView.Build(settings, ungranted ?? [], stored ?? new HashSet<string>(), history ?? []);

    private static HistoryEntry Entry(string indexer, HistoryEvent outcome, int minutesAgo = 5) => new()
    {
        At = Now.AddMinutes(-minutesAgo),
        Event = outcome,
        Key = new EpisodeKey(1, 1, 1),
        ReleaseTitle = "Some.Show.S01E01.1080p",
        Indexer = indexer,
    };

    private static List<PluginFormField> AllFormFields(PluginView view) => [.. PluginNodes.AllFields(view)];

    [Fact]
    public void Build_DrawsOnlyTagsAClientKnows()
    {
        PluginView view = Build(new TorrentDownloaderSettings { Indexers = [new IndexerSettings { Name = "Prowlarr" }] });

        PluginNodes.All(view).Select(node => node.Component)
            .Should().OnlyContain(component => PluginNodes.KnownComponents.Contains(component!));
    }

    [Fact]
    public void Build_CarriesTheTabBar()
    {
        PluginNodes.All(Build(new TorrentDownloaderSettings()))
            .Should().Contain(node => node.Id == "tab-settings");
    }

    // Complete in one go - name, kind and address together - rather than appending a blank
    // entry the owner then has to find and fill in.
    [Fact]
    public void Build_AddsASourceInOneForm()
    {
        PluginView view = Build(new TorrentDownloaderSettings());

        PluginComponent form = PluginNodes.All(view).Should()
            .ContainSingle(node => node.Id == "sources-add-form").Which;

        form.Action!.Payload["method"].Should().Be("AddSource");
        PluginNodes.Fields(form).Select(PluginNodes.Name).Should().BeEquivalentTo(["name", "kind", "url"]);
    }

    [Fact]
    public void Build_RendersASecretFieldAsPassword()
    {
        PluginView view = Build(new TorrentDownloaderSettings { Indexers = [new IndexerSettings { Name = "Prowlarr" }] });

        PluginFormField apiKeyField = AllFormFields(view).Should()
            .ContainSingle(field => PluginNodes.Name(field) == "apiKey").Which;

        PluginNodes.Type(apiKeyField).Should().Be(PluginFormFieldType.Password);
    }

    [Fact]
    public void Build_LeavesASecretFieldEmptyButMarksThatOneIsStored()
    {
        TorrentDownloaderSettings settings = new() { Indexers = [new IndexerSettings { Name = "Prowlarr" }] };
        HashSet<string> stored = new(StringComparer.Ordinal) { SettingsGateway.IndexerSecretKey("Prowlarr") };

        PluginView view = Build(settings, stored: stored);

        PluginFormField apiKeyField = AllFormFields(view).Should()
            .ContainSingle(field => PluginNodes.Name(field) == "apiKey").Which;

        PluginNodes.Value(apiKeyField).Should().BeNull();
        PluginNodes.Placeholder(apiKeyField).Should().Contain("already saved");
    }

    [Fact]
    public void Build_ShowsUngrantedHostsWhenThereAreAny()
    {
        PluginView view = Build(
            new TorrentDownloaderSettings { Indexers = [new IndexerSettings { Name = "Prowlarr" }] },
            ungranted: ["prowlarr.local"]);

        PluginNodes.Says(view, "prowlarr.local").Should().BeTrue();
    }

    // Identified by id, not by "the tree contains no badge at all". The broader version stood
    // in for this one until the page gained a second, unrelated badge - and then failed while
    // the grant warning it was named for was correctly absent.
    [Fact]
    public void Build_OmitsTheGrantWarningWhenEverythingIsGranted()
    {
        PluginView view = Build(new TorrentDownloaderSettings { Indexers = [new IndexerSettings { Name = "Prowlarr" }] });

        PluginNodes.All(view).Should()
            .NotContain(node => node.Id.StartsWith("sources-grant-warning", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_SaysNothingIsConfiguredRatherThanShowingAnEmptyPage()
    {
        PluginView view = Build(new TorrentDownloaderSettings());

        PluginNodes.All(view).Should().Contain(node => node.Id == "sources-empty");
        AllFormFields(view).Should().NotContain(field => PluginNodes.Name(field) == "apiKey");
    }

    [Fact]
    public void Build_HidesTheEmptyStateOnceThereIsASource()
    {
        PluginView view = Build(new TorrentDownloaderSettings { Indexers = [new IndexerSettings { Name = "SceneSource" }] });

        PluginNodes.All(view).Should().NotContain(node => node.Id == "sources-empty");
    }

    // A PluginForm's submit discards the intent's payload and posts only the method plus the
    // form's own field values, so the entry's identity has to be in the method string itself.
    [Fact]
    public void Build_IndexerFormsActionEncodesTheEntrysRenderIndexInTheMethodNotThePayload()
    {
        TorrentDownloaderSettings settings = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr" }, new IndexerSettings { Name = "Jackett" }],
        };

        List<PluginComponent> forms =
        [
            .. PluginNodes.Forms(Build(settings)).Where(form => form.Id.StartsWith("sources-indexer-", StringComparison.Ordinal)),
        ];

        forms.Should().HaveCount(2);

        for (int index = 0; index < forms.Count; index++)
        {
            forms[index].Action!.Payload["method"].Should().Be($"SaveIndexer/{index}");
            forms[index].Action!.Payload["payload"].Should().BeNull();
        }
    }

    // The confirmation is what stands between a misclick and losing a stored credential -
    // asserted on the intent rather than inferred from the button's variant, since a plain
    // button given a red label would look identical at a glance and carry no confirmation.
    [Fact]
    public void Build_RemoveButtonCallsRemoveIndexerWithTheRenderIndexAndAConfirmation()
    {
        TorrentDownloaderSettings settings = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr" }, new IndexerSettings { Name = "Jackett" }],
        };

        PluginComponent remove = PluginNodes.All(Build(settings)).Should()
            .ContainSingle(node => node.Id == "sources-1-remove").Which;

        remove.Component.Should().Be(Ui.ButtonComponent);
        remove.Action!.Payload["method"].Should().Be("RemoveIndexer/1");
        remove.Action.Confirm.Should().NotBeNull();
    }

    // The whole reason sources are their own page. A feed whose URL has quietly started
    // returning nothing looks exactly like a working one until the page says what it has
    // produced.
    [Fact]
    public void Build_SaysHowMuchEachSourceHasYielded()
    {
        TorrentDownloaderSettings settings = new() { Indexers = [new IndexerSettings { Name = "SceneSource" }] };

        PluginView view = Build(
            settings,
            history:
            [
                Entry("SceneSource", HistoryEvent.Imported),
                Entry("SceneSource", HistoryEvent.Imported),
                Entry("SomewhereElse", HistoryEvent.Imported),
            ]);

        PluginNodes.Says(view, "2 episodes imported from this one").Should().BeTrue();
    }

    [Fact]
    public void Build_SaysASourceHasProducedNothingRatherThanShowingAZero()
    {
        TorrentDownloaderSettings settings = new() { Indexers = [new IndexerSettings { Name = "SceneSource" }] };

        PluginView view = Build(settings, history: [Entry("SomewhereElse", HistoryEvent.Imported)]);

        PluginNodes.Says(view, "Nothing from this one yet").Should().BeTrue();
    }

    // Disabled is the difference between "this source found nothing" and "this source was
    // never asked", and those are not the same problem.
    [Fact]
    public void Build_SaysWhenASourceIsTurnedOff()
    {
        TorrentDownloaderSettings settings = new()
        {
            Indexers = [new IndexerSettings { Name = "SceneSource", Enabled = false }],
        };

        PluginNodes.Says(Build(settings), "Disabled").Should().BeTrue();
    }

    // A site's address is not guessable, and the label is the only place the owner learns
    // that a placeholder belongs in it.
    [Fact]
    public void Build_AsksASiteForItsSearchAddress()
    {
        TorrentDownloaderSettings settings = new()
        {
            Indexers = [new IndexerSettings { Name = "TorrentBay", Kind = "site" }],
        };

        PluginFormField url = AllFormFields(Build(settings))
            .Should().ContainSingle(field => PluginNodes.Name(field) == "url" && field.Label.Contains("Search address")).Which;

        url.Label.Should().Contain("{query}");
    }

    // Forms do not survive a page that re-renders under the owner's fingers.
    [Fact]
    public void Build_DoesNotAskTheClientToRefreshWhileSomebodyIsTyping()
    {
        Build(new TorrentDownloaderSettings()).RefreshInterval.Should().Be(0);
    }
}

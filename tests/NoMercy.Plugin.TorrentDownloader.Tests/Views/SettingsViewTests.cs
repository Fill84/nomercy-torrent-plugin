// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text.Json;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

public class SettingsViewTests
{
    // Every test walks the tree through this rather than only checking the root's
    // immediate children: a typo or a duplicate id three levels deep is exactly what
    // Component 2/3's own tests exist to catch, and checking only the top level would miss it.
    private static IEnumerable<PluginComponent> Flatten(IEnumerable<PluginComponent> components)
    {
        foreach (PluginComponent component in components)
        {
            yield return component;

            foreach (PluginComponent descendant in Flatten(component.Items))
            {
                yield return descendant;
            }
        }
    }

    // A form's fields are components now, not a bag of PluginFormField on the form:
    // the design system spends the authored field building an input, a toggle or a
    // select, and the record never reaches the client. Reading them back through the
    // rendered tree is the only version that still sees what a viewer gets.
    private static List<PluginComponent> AllFormFields(PluginView view) => [.. PluginNodes.AllFields(view)];

    [Fact]
    public void Build_ReturnsADeclarativeTreeNotAWebView()
    {
        TorrentDownloaderSettings settings = new();

        PluginView view = SettingsView.Build(settings, [], new HashSet<string>());

        view.Components.Should().NotBeNullOrEmpty();
        view.WebView.Should().BeNull();
    }

    [Fact]
    public void Build_UsesOnlyKnownComponentTags()
    {
        TorrentDownloaderSettings settings = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr" }],
            Clients = [new TorrentClientSettings { Name = "qBittorrent" }],
        };

        PluginView view = SettingsView.Build(settings, ["prowlarr.local"], new HashSet<string>());

        Flatten(view.Components!).Should().OnlyContain(component => PluginNodes.KnownComponents.Contains(component.Component));
    }

    [Fact]
    public void Build_GivesEveryComponentAUniqueId()
    {
        TorrentDownloaderSettings settings = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr" }, new IndexerSettings { Name = "Jackett" }],
            Clients = [new TorrentClientSettings { Name = "qBittorrent" }, new TorrentClientSettings { Name = "Transmission" }],
        };

        PluginView view = SettingsView.Build(settings, ["prowlarr.local"], new HashSet<string>());

        List<string> ids = [.. Flatten(view.Components!).Select(component => component.Id)];
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Build_UsesOnlyKnownFormFieldTypes()
    {
        TorrentDownloaderSettings settings = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr" }],
            Clients = [new TorrentClientSettings { Name = "qBittorrent" }],
        };

        PluginView view = SettingsView.Build(settings, [], new HashSet<string>());

        AllFormFields(view).Should().OnlyContain(field => PluginFormFieldType.All.Contains(PluginNodes.Type(field)));
    }

    [Fact]
    public void Build_RendersASecretFieldAsPassword()
    {
        TorrentDownloaderSettings settings = new() { Indexers = [new IndexerSettings { Name = "Prowlarr" }] };

        PluginView view = SettingsView.Build(settings, [], new HashSet<string>());

        PluginComponent apiKeyField = AllFormFields(view)
            .Should()
            .ContainSingle(field => PluginNodes.Name(field) == "apiKey")
            .Which;

        PluginNodes.Type(apiKeyField).Should().Be(PluginFormFieldType.Password);
    }

    [Fact]
    // Asserts on every password field in the tree, not on one named field, and not by
    // looking for a secret string that Build is never handed in the first place. An
    // absent-value assertion against a value the code under test never receives cannot
    // fail, so it would look like this guard while checking nothing: the moment someone
    // adds a secret field - a client password, a second indexer credential - or starts
    // pre-filling one, that version passes and this one does not.
    public void Build_NeverPutsAValueInAnySecretField()
    {
        TorrentDownloaderSettings settings = new()
        {
            Indexers =
            [
                new IndexerSettings { Name = "Prowlarr" },
                new IndexerSettings { Name = "Jackett" },
            ],
            Clients = [new TorrentClientSettings { Name = "qBit", Username = "admin" }],
        };
        HashSet<string> storedSecretKeys = new(StringComparer.Ordinal)
        {
            SettingsGateway.IndexerSecretKey("Prowlarr"),
            SettingsGateway.ClientSecretKey("qBit"),
        };

        PluginView view = SettingsView.Build(settings, [], storedSecretKeys);

        IReadOnlyList<PluginComponent> secretFields =
        [
            .. AllFormFields(view)
                .Where(field => PluginNodes.Type(field) == PluginFormFieldType.Password),
        ];

        secretFields.Should().HaveCount(3, "every indexer and client gets one secret field");
        secretFields.Should()
            .AllSatisfy(field =>
                (PluginNodes.Value(field) as string ?? string.Empty)
                    .Should()
                    .BeEmpty("a stored secret is never echoed back to the client")
            );
    }

    [Fact]
    public void Build_LeavesASecretFieldEmptyButMarksThatOneIsStored()
    {
        TorrentDownloaderSettings settings = new() { Indexers = [new IndexerSettings { Name = "Prowlarr" }] };
        HashSet<string> storedSecretKeys = new(StringComparer.Ordinal) { SettingsGateway.IndexerSecretKey("Prowlarr") };

        PluginView view = SettingsView.Build(settings, [], storedSecretKeys);

        PluginComponent apiKeyField = AllFormFields(view)
            .Should().ContainSingle(field => PluginNodes.Name(field) == "apiKey").Which;

        PluginNodes.Value(apiKeyField).Should().BeNull();
        PluginNodes.Placeholder(apiKeyField).Should().NotBeNullOrEmpty();
        PluginNodes.Placeholder(apiKeyField).Should().Contain("already saved");
    }

    // Text is a leaf carrying "text" now, and a caption is an NMHelper carrying
    // "helperText" - neither is the "value" prop a view used to author, so a search
    // for that prop finds nothing on a page full of words.
    private static bool Says(PluginView view, string text) =>
        PluginNodes.Words(view).Any(word => word.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void Build_ShowsUngrantedHostsWhenThereAreAny()
    {
        TorrentDownloaderSettings settings = new() { Indexers = [new IndexerSettings { Name = "Prowlarr" }] };

        PluginView view = SettingsView.Build(settings, ["prowlarr.local"], new HashSet<string>());

        Says(view, "prowlarr.local").Should().BeTrue();
    }

    [Fact]
    public void Build_OmitsTheGrantWarningWhenEverythingIsGranted()
    {
        TorrentDownloaderSettings settings = new() { Indexers = [new IndexerSettings { Name = "Prowlarr" }] };

        PluginView view = SettingsView.Build(settings, [], new HashSet<string>());

        // Identified by id, not by "the tree contains no badge at all". The broader version
        // stood in for this one until the page gained a second, unrelated badge - and then
        // failed while the grant warning it was named for was correctly absent.
        Flatten(view.Components!)
            .Should()
            .NotContain(component => component.Id.StartsWith("settings-grant-warning", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_EveryFormUsesAnOrdinarySaveLabelAndCarriesNoReadOnlyNotice()
    {
        TorrentDownloaderSettings settings = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr" }],
            Clients = [new TorrentClientSettings { Name = "qBittorrent" }],
        };

        PluginView view = SettingsView.Build(settings, [], new HashSet<string>());

        IReadOnlyList<PluginComponent> flattened = [.. Flatten(view.Components!)];

        // Saving now has a REST endpoint behind it (TorrentDownloaderSettingsController), so
        // nothing on this page should still tell the owner otherwise.
        flattened.Should().NotContain(component => component.Id == "settings-readonly-notice");
        flattened.Should().NotContain(component => component.Id == "settings-readonly-badge");

        // A form is no longer findable by tag - the design system draws it as the same
        // NMCard as every other container, and the submit label it used to carry as a
        // prop is now the button's own words. So a form is the card that owns a submit
        // button, and the label is read off that button.
        IReadOnlyList<PluginComponent> forms = [.. PluginNodes.Forms(view)];
        forms.Should().HaveCount(3, "one general form plus one per configured indexer and client");
        forms.Should().AllSatisfy(form =>
            PluginNodes.Words(PluginNodes.Submit(form)).Should().Contain("Save"));
    }

    [Fact]
    public void Build_ShowsAnEmptyStateWhenNoDownloadClientIsConfigured()
    {
        TorrentDownloaderSettings settings = new() { Indexers = [new IndexerSettings { Name = "Prowlarr" }] };

        PluginView view = SettingsView.Build(settings, [], new HashSet<string>());

        Flatten(view.Components!).Should().Contain(component => component.Id == "settings-clients-empty");
    }

    [Fact]
    public void Build_ShowsAnEmptyStateWhenNoIndexerIsConfigured()
    {
        TorrentDownloaderSettings settings = new();

        PluginView view = SettingsView.Build(settings, [], new HashSet<string>());

        Flatten(view.Components!).Should().Contain(component => component.Component == PluginComponentType.EmptyState);
        AllFormFields(view).Should().NotContain(field => PluginNodes.Name(field) == "apiKey");
    }

    // --- private trackers ---------------------------------------------------------

    // The passkey in an announce URL is the account. Build is never handed the stored
    // value, so this asserts the field is shaped to receive one and never to show one -
    // a rendered passkey is a passkey in a browser cache, a screenshot and a support
    // ticket, and it is the same field the owner has to be able to replace.
    [Fact]
    public void Build_RendersAPrivateTrackersAnnounceUrlAsASecretThatIsNeverSentBack()
    {
        TorrentDownloaderSettings settings = new()
        {
            PrivateTrackers = [new PrivateTrackerSettings { Name = "RedFish" }],
        };
        HashSet<string> stored = new(StringComparer.Ordinal) { SettingsGateway.PrivateTrackerAnnounceKey("RedFish") };

        PluginView view = SettingsView.Build(settings, [], stored);

        PluginComponent field = AllFormFields(view).Should()
            .ContainSingle(field => PluginNodes.Name(field) == "announceUrl").Which;

        PluginNodes.Type(field).Should().Be(PluginFormFieldType.Password);
        PluginNodes.Value(field).Should().BeNull();
        PluginNodes.Placeholder(field).Should().Contain("already saved");
    }

    [Fact]
    public void Build_SaysAPrivateTrackerIsNotConfiguredRatherThanShowingNothing()
    {
        PluginView view = SettingsView.Build(new TorrentDownloaderSettings(), [], new HashSet<string>());

        // "Nothing here" reads as broken. The empty state has to say what the absence
        // means, and what it means is that nothing will ever be uploaded.
        Flatten(view.Components!).Should().Contain(component => component.Id == "settings-trackers-empty");
    }

    // Same defect the indexer forms had: a PluginForm's submit discards the intent's
    // payload, so an entry's identity has to ride in the method string or the wrong
    // tracker is edited.
    [Fact]
    public void Build_PrivateTrackerFormsActionEncodesTheEntrysRenderIndexInTheMethod()
    {
        TorrentDownloaderSettings settings = new()
        {
            PrivateTrackers = [new PrivateTrackerSettings { Name = "RedFish" }, new PrivateTrackerSettings { Name = "BlueFish" }],
        };

        PluginView view = SettingsView.Build(settings, [], new HashSet<string>());

        PluginComponent second = Flatten(view.Components!).Single(component => component.Id == "settings-tracker-1-form");
        second.Action!.Payload["method"].Should().Be("SavePrivateTracker/1");
    }

    // Specials are off by default and the owner has to be able to see that, and change it.
    // A default that can only be changed by editing config.json is not a setting.
    [Fact]
    public void Build_OffersTheSpecialsToggleShowingWhatIsCurrentlySet()
    {
        PluginView view = SettingsView.Build(new TorrentDownloaderSettings(), [], new HashSet<string>());

        PluginComponent field = AllFormFields(view).Should()
            .ContainSingle(field => PluginNodes.Name(field) == "includeSpecials").Which;

        PluginNodes.Type(field).Should().Be(PluginFormFieldType.Toggle);
        PluginNodes.Checked(field).Should().BeFalse();
    }

    [Fact]
    public void Build_ShowsTheSpecialsToggleOnWhenItIsOn()
    {
        PluginView view = SettingsView.Build(
            new TorrentDownloaderSettings { IncludeSpecials = true },
            [],
            new HashSet<string>());

        PluginComponent field = AllFormFields(view).Single(field => PluginNodes.Name(field) == "includeSpecials");

        PluginNodes.Checked(field).Should().BeTrue();
    }

    // The general form's action carries a method the client can resolve without help: no
    // identifying field, no payload of its own.
    [Fact]
    public void Build_GeneralFormsActionIsPlainSaveSettingsWithNoPayload()
    {
        TorrentDownloaderSettings settings = new();

        PluginView view = SettingsView.Build(settings, [], new HashSet<string>());

        PluginComponent form = Flatten(view.Components!).Should().ContainSingle(component => component.Id == "settings-general-form").Which;
        form.Action!.Payload["method"].Should().Be("SaveSettings");
        form.Action.Payload["payload"].Should().BeNull();
    }

    // The fix in one assertion: an indexer/client form's identity has to survive the
    // client's PluginForm, which discards the intent's payload on submit and posts only
    // the method plus the form's own field values. Putting the render index in the method
    // string itself - not in a payload dictionary - is what a PluginForm submit cannot
    // strip away.
    [Fact]
    public void Build_IndexerFormsActionEncodesTheEntrysRenderIndexInTheMethodNotThePayload()
    {
        TorrentDownloaderSettings settings = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr" }, new IndexerSettings { Name = "Jackett" }],
        };

        PluginView view = SettingsView.Build(settings, [], new HashSet<string>());

        // The forms themselves, not everything under them: a field's id extends its
        // form's, so a plain prefix match over the flattened tree counts one form once
        // per control it draws.
        List<PluginComponent> forms =
        [
            .. PluginNodes.Forms(view).Where(form => form.Id.StartsWith("settings-indexer-", StringComparison.Ordinal)),
        ];
        forms.Should().HaveCount(2);
        for (int i = 0; i < forms.Count; i++)
        {
            forms[i].Action!.Payload["method"].Should().Be($"SaveIndexer/{i}");
            forms[i].Action!.Payload["payload"].Should().BeNull();
        }
    }

    [Fact]
    public void Build_ClientFormsActionEncodesTheEntrysRenderIndexInTheMethodNotThePayload()
    {
        TorrentDownloaderSettings settings = new()
        {
            Clients = [new TorrentClientSettings { Name = "qBit" }, new TorrentClientSettings { Name = "Transmission" }],
        };

        PluginView view = SettingsView.Build(settings, [], new HashSet<string>());

        // The forms themselves, not everything under them: a field's id extends its
        // form's, so a plain prefix match over the flattened tree counts one form once
        // per control it draws.
        List<PluginComponent> forms =
        [
            .. PluginNodes.Forms(view).Where(form => form.Id.StartsWith("settings-client-", StringComparison.Ordinal)),
        ];
        forms.Should().HaveCount(2);
        for (int i = 0; i < forms.Count; i++)
        {
            forms[i].Action!.Payload["method"].Should().Be($"SaveClient/{i}");
            forms[i].Action!.Payload["payload"].Should().BeNull();
        }
    }

    [Fact]
    public void Build_HidesTheIndexersEmptyStateOnceThereIsAtLeastOneIndexer()
    {
        TorrentDownloaderSettings settings = new() { Indexers = [new IndexerSettings { Name = "New Indexer 1" }] };

        PluginView view = SettingsView.Build(settings, [], new HashSet<string>());

        Flatten(view.Components!).Should().NotContain(component => component.Id == "settings-indexers-empty");
    }

    [Fact]
    public void Build_HidesTheClientsEmptyStateOnceThereIsAtLeastOneClient()
    {
        TorrentDownloaderSettings settings = new() { Clients = [new TorrentClientSettings { Name = "New Download Client 1" }] };

        PluginView view = SettingsView.Build(settings, [], new HashSet<string>());

        Flatten(view.Components!).Should().NotContain(component => component.Id == "settings-clients-empty");
    }

    [Fact]
    public void Build_AddIndexerButtonCallsAddIndexerWithNoConfirmationAndNoPayload()
    {
        TorrentDownloaderSettings settings = new();

        PluginView view = SettingsView.Build(settings, [], new HashSet<string>());

        PluginComponent addButton = Flatten(view.Components!).Should().ContainSingle(component => component.Id == "settings-indexers-add").Which;
        addButton.Component.Should().Be(PluginComponentType.Button);
        addButton.Action!.Payload["method"].Should().Be("AddIndexer");
        addButton.Action.Payload["payload"].Should().BeNull();
        addButton.Action.Confirm.Should().BeNull();
    }

    [Fact]
    public void Build_AddClientButtonCallsAddClientWithNoConfirmationAndNoPayload()
    {
        TorrentDownloaderSettings settings = new();

        PluginView view = SettingsView.Build(settings, [], new HashSet<string>());

        PluginComponent addButton = Flatten(view.Components!).Should().ContainSingle(component => component.Id == "settings-clients-add").Which;
        addButton.Component.Should().Be(PluginComponentType.Button);
        addButton.Action!.Payload["method"].Should().Be("AddClient");
        addButton.Action.Confirm.Should().BeNull();
    }

    // The confirmation is what stands between a misclick and losing a stored credential -
    // asserted directly on the intent rather than inferred from the button's variant, since
    // a plain Button given a red label would look identical to a DestructiveButton at a
    // glance but carry no PluginConfirmation at all.
    [Fact]
    public void Build_RemoveIndexerButtonCallsRemoveIndexerWithTheRenderIndexAndAConfirmation()
    {
        TorrentDownloaderSettings settings = new()
        {
            Indexers = [new IndexerSettings { Name = "Prowlarr" }, new IndexerSettings { Name = "Jackett" }],
        };

        PluginView view = SettingsView.Build(settings, [], new HashSet<string>());

        PluginComponent removeButton = Flatten(view.Components!).Should().ContainSingle(component => component.Id == "indexer-1-remove").Which;
        removeButton.Component.Should().Be(PluginComponentType.Button);
        removeButton.Action!.Payload["method"].Should().Be("RemoveIndexer/1");
        removeButton.Action.Confirm.Should().NotBeNull();
    }

    [Fact]
    public void Build_RemoveClientButtonCallsRemoveClientWithTheRenderIndexAndAConfirmation()
    {
        TorrentDownloaderSettings settings = new()
        {
            Clients = [new TorrentClientSettings { Name = "qBit" }],
        };

        PluginView view = SettingsView.Build(settings, [], new HashSet<string>());

        PluginComponent removeButton = Flatten(view.Components!).Should().ContainSingle(component => component.Id == "client-0-remove").Which;
        removeButton.Action!.Payload["method"].Should().Be("RemoveClient/0");
        removeButton.Action.Confirm.Should().NotBeNull();
    }

    [Fact]
    public void Build_RendersNotSavedYetWhenNoTimestampIsRecorded()
    {
        TorrentDownloaderSettings settings = new();

        PluginView view = SettingsView.Build(settings, [], new HashSet<string>());

        Says(view, "Not saved yet").Should().BeTrue();
    }

    [Fact]
    public void Build_RendersTheSavedTimestampInInvariantCultureWhenSet()
    {
        TorrentDownloaderSettings settings = new() { LastSavedAtUtc = new DateTimeOffset(2026, 7, 31, 1, 59, 0, TimeSpan.Zero) };

        PluginView view = SettingsView.Build(settings, [], new HashSet<string>());

        Says(view, "2026-07-31 01:59").Should().BeTrue();
    }
}

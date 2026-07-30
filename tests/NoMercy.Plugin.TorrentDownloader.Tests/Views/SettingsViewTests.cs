// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text.Json;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Configuration;
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

    private static IEnumerable<PluginFormField> AllFormFields(IEnumerable<PluginComponent> components)
    {
        foreach (PluginComponent component in Flatten(components))
        {
            if (component.Props.TryGetValue("fields", out object? fields) && fields is IEnumerable<PluginFormField> formFields)
            {
                foreach (PluginFormField field in formFields)
                {
                    yield return field;
                }
            }
        }
    }

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

        Flatten(view.Components!).Should().OnlyContain(component => PluginComponentType.All.Contains(component.Component));
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

        AllFormFields(view.Components!).Should().OnlyContain(field => PluginFormFieldType.All.Contains(field.Type));
    }

    [Fact]
    public void Build_RendersASecretFieldAsPassword()
    {
        TorrentDownloaderSettings settings = new() { Indexers = [new IndexerSettings { Name = "Prowlarr" }] };

        PluginView view = SettingsView.Build(settings, [], new HashSet<string>());

        AllFormFields(view.Components!)
            .Should()
            .ContainSingle(field => field.Name == "apiKey")
            .Which.Type.Should()
            .Be(PluginFormFieldType.Password);
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

        IReadOnlyList<PluginFormField> secretFields =
        [
            .. AllFormFields(view.Components!)
                .Where(field => field.Type == PluginFormFieldType.Password),
        ];

        secretFields.Should().HaveCount(3, "every indexer and client gets one secret field");
        secretFields.Should()
            .AllSatisfy(field =>
                (field.Value as string ?? string.Empty)
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

        PluginFormField apiKeyField = AllFormFields(view.Components!).Should().ContainSingle(field => field.Name == "apiKey").Which;
        apiKeyField.Value.Should().BeNull();
        apiKeyField.Placeholder.Should().NotBeNullOrEmpty();
        apiKeyField.Placeholder.Should().Contain("already saved");
    }

    private static bool IsTextMentioning(PluginComponent component, string text)
    {
        return component.Component == PluginComponentType.Text
            && component.Props.TryGetValue("value", out object? value)
            && value is string stringValue
            && stringValue.Contains(text, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ShowsUngrantedHostsWhenThereAreAny()
    {
        TorrentDownloaderSettings settings = new() { Indexers = [new IndexerSettings { Name = "Prowlarr" }] };

        PluginView view = SettingsView.Build(settings, ["prowlarr.local"], new HashSet<string>());

        Flatten(view.Components!).Should().Contain(component => IsTextMentioning(component, "prowlarr.local"));
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

        IReadOnlyList<PluginComponent> forms =
            [.. flattened.Where(component => component.Component == PluginComponentType.Form)];
        forms.Should().HaveCount(3, "one general form plus one per configured indexer and client");
        forms.Should().AllSatisfy(form => form.Props["submitLabel"].Should().Be("Save"));
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
        AllFormFields(view.Components!).Should().NotContain(field => field.Name == "apiKey");
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

        List<PluginComponent> forms =
        [
            .. Flatten(view.Components!).Where(component => component.Id.StartsWith("settings-indexer-", StringComparison.Ordinal)),
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

        List<PluginComponent> forms =
        [
            .. Flatten(view.Components!).Where(component => component.Id.StartsWith("settings-client-", StringComparison.Ordinal)),
        ];
        forms.Should().HaveCount(2);
        for (int i = 0; i < forms.Count; i++)
        {
            forms[i].Action!.Payload["method"].Should().Be($"SaveClient/{i}");
            forms[i].Action!.Payload["payload"].Should().BeNull();
        }
    }
}

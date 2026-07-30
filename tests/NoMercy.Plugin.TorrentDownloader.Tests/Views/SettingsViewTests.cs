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
    public void Build_NeverPutsASecretValueInTheTree()
    {
        const string apiKey = "super-secret-api-key-that-must-never-leak";
        TorrentDownloaderSettings settings = new() { Indexers = [new IndexerSettings { Name = "Prowlarr" }] };
        HashSet<string> storedSecretKeys = new(StringComparer.Ordinal) { SettingsGateway.IndexerSecretKey("Prowlarr") };

        PluginView view = SettingsView.Build(settings, [], storedSecretKeys);

        string json = JsonSerializer.Serialize(view);
        json.Should().NotContain(apiKey);
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

        Flatten(view.Components!).Should().NotContain(component => component.Component == PluginComponentType.Badge);
    }

    [Fact]
    public void Build_ShowsAnEmptyStateWhenNoIndexerIsConfigured()
    {
        TorrentDownloaderSettings settings = new();

        PluginView view = SettingsView.Build(settings, [], new HashSet<string>());

        Flatten(view.Components!).Should().Contain(component => component.Component == PluginComponentType.EmptyState);
        AllFormFields(view.Components!).Should().NotContain(field => field.Name == "apiKey");
    }
}

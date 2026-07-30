// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text.Json;
using FluentAssertions;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

public class ManifestTests
{
    private static readonly HashSet<string> KnownHooks = new(StringComparer.OrdinalIgnoreCase)
    {
        PluginHookCapability.MediaSource,
        PluginHookCapability.Metadata,
        PluginHookCapability.ScheduledTask,
        PluginHookCapability.Auth,
        PluginHookCapability.Encoder,
        PluginHookCapability.Ui,
        PluginHookCapability.LibraryWrite,
    };

    // Internal so DiscoveryContractTests reads the manifest the same way rather than
    // duplicating the load - two readers that could disagree about which file is the real one
    // would defeat the point of asserting they agree.
    internal static PluginManifest LoadManifest()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "plugin.json");
        string json = File.ReadAllText(path);
        PluginManifest? manifest = JsonSerializer.Deserialize<PluginManifest>(json);

        manifest.Should().NotBeNull();

        return manifest!;
    }

    [Fact]
    public void Manifest_DeserialisesWithTheHostsOwnType()
    {
        PluginManifest manifest = LoadManifest();

        manifest.Id.Should().NotBeEmpty();
        manifest.Name.Should().NotBeNullOrWhiteSpace();
        manifest.Description.Should().NotBeNullOrWhiteSpace();
        manifest.Version.Should().NotBeNullOrWhiteSpace();
        manifest.Assembly.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Manifest_IdMatchesPluginIdentity()
    {
        PluginManifest manifest = LoadManifest();

        manifest.Id.Should().Be(PluginIdentity.Id);
    }

    [Fact]
    public void Manifest_NameAndDescriptionMatchPluginIdentity()
    {
        PluginManifest manifest = LoadManifest();

        manifest.Name.Should().Be(PluginIdentity.Name);
        manifest.Description.Should().Be(PluginIdentity.Description);
    }

    [Fact]
    public void Manifest_VersionMatchesPluginIdentity()
    {
        PluginManifest manifest = LoadManifest();

        Version.Parse(manifest.Version).Should().Be(PluginIdentity.Version);
    }

    [Fact]
    public void Manifest_AssemblyNameMatchesTheBuiltAssembly()
    {
        PluginManifest manifest = LoadManifest();

        manifest.Assembly.Should().Be(PluginIdentity.AssemblyFileName);

        string assemblyPath = Path.Combine(AppContext.BaseDirectory, manifest.Assembly);
        File.Exists(assemblyPath).Should().BeTrue();
    }

    [Fact]
    public void Manifest_TargetAbiIsCompatibleWithTheShippedAbi()
    {
        PluginManifest manifest = LoadManifest();

        PluginAbi.IsCompatible(manifest.TargetAbi).Should().BeTrue();
    }

    [Fact]
    public void Manifest_DeclaresOnlyTheHooksThisStageImplements()
    {
        PluginManifest manifest = LoadManifest();
        List<string> hooks = manifest.Capabilities!.Hooks;

        hooks.Should().Equal(PluginHookCapability.ScheduledTask, PluginHookCapability.Ui);
        hooks.Should().OnlyContain(hook => KnownHooks.Contains(hook));
    }

    [Fact]
    public void Manifest_DeclaresNoElevatedHook()
    {
        PluginManifest manifest = LoadManifest();
        List<string> hooks = manifest.Capabilities!.Hooks;

        hooks.Should().NotContain(hook => PluginHookCapability.Elevated.Contains(hook));
    }

    // Rest flipped true with this stage's REST surface (TorrentDownloaderSettingsController).
    // Ws stays false: nothing in this stage implements IPluginHubHandler, and turning it on
    // with no handler behind it would be the same false promise this manifest used to make
    // about saving.
    [Fact]
    public void Manifest_DeclaresRestNowThatItHasAControllerButNotWs()
    {
        PluginManifest manifest = LoadManifest();

        manifest.Capabilities!.Rest.Should().BeTrue();
        manifest.Capabilities.Ws.Should().BeFalse();
    }

    [Fact]
    public void Manifest_UiMountUsesAKnownSection()
    {
        PluginManifest manifest = LoadManifest();
        PluginUiMount mount = manifest.Capabilities!.Ui!.Mounts[0];

        PluginUiSection.All.Should().Contain(mount.Section);
    }

    [Fact]
    public void Manifest_UiMountAgreesWithNavEntries()
    {
        PluginManifest manifest = LoadManifest();
        PluginUiMount mount = manifest.Capabilities!.Ui!.Mounts[0];
        TorrentDownloaderPlugin plugin = new();
        PluginNavEntry navEntry = plugin.NavEntries.Should().ContainSingle().Which;

        navEntry.Section.Should().Be(mount.Section);
        navEntry.Route.Should().Be(mount.Route);
    }
}

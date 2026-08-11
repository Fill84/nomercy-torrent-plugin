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

        manifest.Id.Should().NotBe(default(Ulid));
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

    // autoEnabled is NOT the consent gate, which is the easy misreading and was the bug:
    // PluginConsentService.IsBaseline returns false as soon as a plugin declares rest, ws or
    // network, so the server holds this plugin at Disabled until the owner consents no matter
    // what autoEnabled says. What autoEnabled actually controls is whether it comes back by
    // itself afterwards - PluginLoader computes
    //     mayAutoEnable = manifest.AutoEnabled && (IsBaseline(caps) || HasConsent(id))
    // so with false the plugin drops back to Disabled on every restart and the owner has to
    // re-enable it each time, having already granted consent once. Verified against a real
    // install: consent was granted, the server restarted, and it did not come back.
    [Fact]
    public void Manifest_AutoEnablesSoConsentSurvivesARestart()
    {
        PluginManifest manifest = LoadManifest();

        manifest.AutoEnabled.Should().BeTrue();

        // The safety this plugin needs comes from being non-baseline, not from autoEnabled.
        // If rest is ever dropped, autoEnabled: true would start meaning "run on install with
        // no prompt" - so the two are asserted together.
        manifest.Capabilities!.Rest.Should()
            .BeTrue("rest is what makes this plugin elevated, and therefore consent-gated");
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
    public void Manifest_EveryUiMountUsesAKnownSection()
    {
        PluginManifest manifest = LoadManifest();

        // An unknown section is not rejected - it silently falls back to the add-ons page,
        // which is a mount the author cannot find and is never told about.
        manifest.Capabilities!.Ui!.Mounts.Should().OnlyContain(mount => PluginUiSection.All.Contains(mount.Section));
    }

    [Fact]
    public void Manifest_UiMountsAgreeWithNavEntries()
    {
        PluginManifest manifest = LoadManifest();
        List<PluginUiMount> mounts = manifest.Capabilities!.Ui!.Mounts;
        TorrentDownloaderPlugin plugin = new();

        plugin.NavEntries.Select(entry => (entry.Section, entry.Route))
            .Should().BeEquivalentTo(mounts.Select(mount => (mount.Section, mount.Route)));
    }

    // The dashboard prefers NavEntries over the manifest, so a page mounted in only one of
    // the two is a page that appears for some clients and not others.
    [Fact]
    public void Manifest_MountsBothThePluginsPages()
    {
        PluginManifest manifest = LoadManifest();
        List<PluginUiMount> mounts = manifest.Capabilities!.Ui!.Mounts;

        // One entry that is the plugin, landing on the overview with the rest behind the tab
        // bar, and one straight to the settings page for the dashboard's settings section.
        mounts.Select(mount => mount.Route).Should().BeEquivalentTo(["/settings", "/"]);
    }
}

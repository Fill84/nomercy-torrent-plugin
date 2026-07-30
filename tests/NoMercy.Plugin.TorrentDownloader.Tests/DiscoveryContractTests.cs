// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Reflection;
using FluentAssertions;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

// Pins the steps the server's PluginManager actually performs to load a plugin, taken from
// the documented discovery contract that the Internet Radio plugin follows:
//
//   1. Scan <server>/plugins/<folder>/plugin.json.
//   2. Load the assembly named by the manifest's "assembly" field.
//   3. Reflect every public, non-abstract type assignable to IPlugin.
//   4. Instantiate it with Activator.CreateInstance - so it MUST have a public
//      parameterless constructor.
//   5. Call Initialize(IPluginContext) once.
//   6. Pick up specialised interfaces via GetPluginsOfType<T>().
//
// Every one of these is invisible to an ordinary unit test, because a test constructs the
// plugin directly with `new`. The failure they guard against is the plugin refusing to load
// at all, in a way that only shows up on a real server: give the entry class a constructor
// parameter - the obvious move the day it wants a dependency injected - and step 4 throws
// MissingMethodException while all 332 other tests still pass.
public class DiscoveryContractTests
{
    private static Assembly PluginAssembly => typeof(TorrentDownloaderPlugin).Assembly;

    private static IReadOnlyList<Type> DiscoverablePluginTypes =>
        [
            .. PluginAssembly
                .GetTypes()
                .Where(type =>
                    type.IsPublic && !type.IsAbstract && typeof(IPlugin).IsAssignableFrom(type)
                ),
        ];

    [Fact]
    public void Assembly_ExposesExactlyOneDiscoverablePluginType()
    {
        // More than one would have the server load a second plugin from this assembly under
        // the same manifest id; none would have it load nothing and report no error.
        DiscoverablePluginTypes.Should().ContainSingle().Which.Should().Be<TorrentDownloaderPlugin>();
    }

    [Fact]
    public void EntryType_HasAPublicParameterlessConstructor()
    {
        // Step 4. Asserted on the reflected constructor rather than by calling `new`, because
        // `new` is what the compiler resolves and Activator is what the server uses.
        ConstructorInfo? constructor = typeof(TorrentDownloaderPlugin).GetConstructor(
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: [],
            modifiers: null
        );

        constructor.Should().NotBeNull("the server instantiates plugins with Activator.CreateInstance");
    }

    [Fact]
    public void EntryType_CanBeCreatedTheWayTheServerCreatesIt()
    {
        object? created = Activator.CreateInstance(typeof(TorrentDownloaderPlugin));

        created.Should().BeOfType<TorrentDownloaderPlugin>();
        ((IDisposable)created!).Dispose();
    }

    [Fact]
    public void EntryType_ImplementsEveryInterfaceItsManifestClaims()
    {
        // Step 6: specialised interfaces are found by reflecting the TYPE, not by reading the
        // manifest, so a hook declared in plugin.json with no matching interface is silently
        // never picked up. This ties the two together in the one direction that can drift.
        PluginManifest manifest = ManifestTests.LoadManifest();
        IReadOnlyList<string> hooks = manifest.Capabilities?.Hooks ?? [];

        foreach (string hook in hooks)
        {
            Type expected = hook switch
            {
                PluginHookCapability.ScheduledTask => typeof(IScheduledTaskPlugin),
                PluginHookCapability.Ui => typeof(IUiPlugin),
                PluginHookCapability.MediaSource => typeof(IMediaSourcePlugin),
                PluginHookCapability.Metadata => typeof(IMetadataPlugin),
                PluginHookCapability.Auth => typeof(IAuthPlugin),
                PluginHookCapability.Encoder => typeof(IEncoderPlugin),
                _ => typeof(IPlugin),
            };

            expected
                .IsAssignableFrom(typeof(TorrentDownloaderPlugin))
                .Should()
                .BeTrue($"plugin.json declares the '{hook}' hook, so the entry type must implement {expected.Name}");
        }
    }

    [Fact]
    public void EntryType_IdMatchesTheManifestTheServerKeysLifecycleOn()
    {
        // The server uses this id as the plugin's lifecycle identity across restarts, so a
        // mismatch between the assembly and the manifest is not a cosmetic drift.
        TorrentDownloaderPlugin plugin = new();

        try
        {
            plugin.Id.Should().Be(ManifestTests.LoadManifest().Id);
        }
        finally
        {
            plugin.Dispose();
        }
    }
}

# plugins: updating a plugin while the server runs keeps serving the old copy's controllers, so every button of the plugin stops working

**Filed as media-server [#60](https://github.com/NoMercy-Entertainment/nomercy-media-server/issues/60).**

Raised from `nomercy-torrent-refactor-plugin`, 17 September 2026. Read against media-server `dev` at
`ca151d626`. Seen on the owner's server (`0.1.506`).

**This repository is read-only to the plugin's author.** The issue is written so it can be picked up and
carried out in one pass: one method changes, one test is added.

## What happens

1. The server starts and loads plugin X, version A. `PluginRouteSubscriber.OnLoaded` calls
   `PluginApplicationPartRegistrar.Attach`, which adds A's assembly as an `AssemblyPart`. X's REST endpoints
   work.
2. The owner updates X to version B from the catalogue, without a restart. The loader creates B in a new
   `PluginLoadContext`, stores it in the registry and publishes `PluginLoadedEvent`
   (`src/NoMercy.Plugins/PluginLoader.cs:519-530`).
3. `PluginRouteSubscriber.OnLoaded` calls `Attach` again. `AttachPart` returns at once, because the plugin
   id is already in `_attached`:

   ```csharp
   // src/NoMercy.Api/Plugins/PluginApplicationPartRegistrar.cs:83-86
   private bool AttachPart(PluginInfo info, IPluginManager pluginManager)
   {
       if (_attached.ContainsKey(info.Id))
           return false;
   ```

4. MVC keeps serving **A's controllers**. A controller asks `IPluginManager.GetPluginInstance(id)` and gets
   **B's instance**. B's plugin class and A's plugin class have the same name but come from two load
   contexts, so `instance is XPlugin` is false and the controller cannot reach the plugin.

**Result:** after an update from the catalogue, every button on the plugin's pages does nothing until the
server is restarted. For the torrent plugin that was Cancel, Pause, Resume, Run, Stop and every Save, on
17 September 2026. A second effect: A's assembly stays referenced by the part manager, so A's collectible
load context can never unload.

## The fix

`AttachPart` should compare the attached assembly with the running instance's assembly, and replace the
part when they differ. Nothing else has to change: `PluginRouteSubscriber` already calls `Attach` on every
`PluginLoadedEvent`, and `PluginRouteConvention` reads `OwnerOf`, which follows `_attached`.

```csharp
// src/NoMercy.Api/Plugins/PluginApplicationPartRegistrar.cs
private bool AttachPart(PluginInfo info, IPluginManager pluginManager)
{
    if (info.Capabilities?.Rest != true)
        return false;

    Assembly? assembly = pluginManager.GetPluginInstance(info.Id)?.GetType().Assembly;

    if (assembly is null || !CarriesControllers(assembly))
        return false;

    if (_attached.TryGetValue(info.Id, out Assembly? attached))
    {
        // The running copy's controllers are already served: nothing to do.
        if (ReferenceEquals(attached, assembly))
            return false;

        // An update loaded a new copy beside the old one. Serve the new copy's controllers,
        // and let go of the old assembly so its load context can unload.
        ApplicationPart? old = partManager.ApplicationParts.FirstOrDefault(candidate =>
            candidate is AssemblyPart assemblyPart && ReferenceEquals(assemblyPart.Assembly, attached));

        if (old is not null)
            partManager.ApplicationParts.Remove(old);
    }

    partManager.ApplicationParts.Add(new AssemblyPart(assembly));
    _attached[info.Id] = assembly;

    logger.LogInformation(
        "Attached controllers from plugin {PluginName} ({PluginId}).",
        info.Name,
        info.Id
    );

    return true;
}
```

Note the order change: the capability and assembly checks now come before the "already attached" check,
because that check needs the running assembly. `Attach` and `AttachAll` call `changeProvider.TriggerChange()`
when this returns true, so the route table is rebuilt once.

## A test

In `tests/NoMercy.Tests.Api` (next to `PluginRestEndpointRoutingTests`): attach a plugin, then attach again
with the plugin manager answering an instance whose type comes from a **different assembly** carrying a
controller, and assert that

- the second `Attach` returns `true`;
- `OwnerOf(newAssembly)` returns the plugin id, and `OwnerOf(oldAssembly)` returns `null`;
- `partManager.ApplicationParts` holds the new assembly's part and not the old one's;
- attaching a third time with the same instance returns `false` and changes nothing.

The two assemblies need a `PluginControllerBase` controller each, because `AttachPart` skips an assembly
without one (`CarriesControllers`). The test assembly itself has one already (the controller used by
`PluginRestEndpointRoutingTests`); the second can be a small test-only project with one empty
`PluginControllerBase` controller and one `IPlugin`. The sample plugins `NoMercy.Plugin.Samples.Echo` and
`EchoNext` carry no controller, so they cannot be used for this as they are.

## How to check it on a running server

1. Install any plugin with REST endpoints, for example the torrent downloader, and use one of its buttons.
2. Update it from the catalogue without restarting.
3. Use the same button. Before the fix: nothing happens. The torrent plugin's endpoints answer 404 with
   "They are the same class from two load contexts, which is what an update loaded beside the old copy leaves
   behind". After the fix: it works, and the log shows "Attached controllers from plugin …" a second time.

## Filed twice, from two plugins

It is **media-server #60** (this analysis, from the torrent plugin) and **media-server #71**, filed on
v0.1.526 after installing Automix 0.1.3 over 0.1.2 from the dashboard. Same cause, named at the same
line, and the same fix shape: on `PluginLoadedEvent`, detach the part when `_attached` holds the id
against a different assembly. Both were open on 22 September 2026; neither needs another issue.

## The plugin no longer works around it

From 0.6.4 to 0.7.0 the torrent plugin did this itself: it resolved `PluginApplicationPartRegistrar` from the
server's container by name and called `Detach` and `Attach` when `OwnerOf(runningAssembly)` was not its own
id. On 22 September 2026 that was removed, because it is a route into the server outside the SDK that the
owner can neither see nor revoke (FiLL/nomercy-torrent-plugin#1). Until this issue is fixed, a plugin
updated while the server runs answers every button with "Restart the server", and a restart puts it right.

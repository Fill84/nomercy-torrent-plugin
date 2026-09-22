using System.Reflection;
using Microsoft.Extensions.Logging;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>Makes the server serve this plugin's endpoints from the copy of it that is running.</summary>
/// <remarks>
/// <para>
/// <strong>Every button stopped answering after an update through the catalogue.</strong> On 17 September 2026
/// the owner updated to 0.6.2 and to 0.6.3 without restarting, and Cancel, Pause, Resume, Run and Save did
/// nothing at all. The server attaches a plugin's controllers when the plugin is loaded, and its registrar returns
/// at once for a plugin id it has attached before — so the controllers of the copy the server started with went
/// on answering, found the new copy's plugin to be a type from another load context, and refused every request.
/// </para>
/// <para>
/// The media server is not this plugin's to change. What it offers is enough: the registrar is in the server's
/// container, says which plugin an assembly's controllers belong to, and detaches and attaches them. Reached by
/// name, and a server without it is said, never thrown at.
/// </para>
/// <para>
/// <strong>From a controller, on contract 12.</strong> The plugin used to do this the moment the server said it
/// had loaded, through a container the context handed it; contract 12 hands a plugin neither the event nor the
/// container. A controller still has both — it is built by the server's own container, on a request — and the
/// controller that finds itself stale is exactly the one an update left behind. So <see cref="Controllers.LivePlugin"/>
/// does it on the first press after an update, and answers that press with "press again".
/// </para>
/// </remarks>
public sealed class OwnEndpoints(IServiceProvider services, ILogger logger)
{
    private const string RegistrarType = "NoMercy.Api.Plugins.PluginApplicationPartRegistrar";

    /// <summary>Re-attaches this plugin's controllers when the server is still serving an older copy's.</summary>
    /// <remarks>
    /// Judged against the copy the server holds as running, not against whichever copy asks: an older copy still
    /// listening as it is unloaded would otherwise detach and attach again for nothing.
    /// </remarks>
    /// <returns>Whether they were attached again.</returns>
    public bool ServeCurrent(Ulid pluginId)
    {
        try
        {
            if (services.GetService(typeof(IPluginManager)) is not IPluginManager manager
                || Find(RegistrarType) is not Type type
                || services.GetService(type) is not object registrar
                || manager.GetPluginInstance(pluginId)?.GetType().Assembly is not Assembly running)
            {
                return false;
            }

            if (Method(type, "OwnerOf", 1)?.Invoke(registrar, [running]) is object owner
                && string.Equals(owner.ToString(), pluginId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                // Already serving the running copy: every ordinary start. Detaching and attaching again would
                // rebuild the server's whole route table for nothing.
                return false;
            }

            if (manager.GetPluginInfo(pluginId) is not PluginInfo info
                || Method(type, "Detach", 1) is not MethodInfo detach
                || Method(type, "Attach", 2) is not MethodInfo attach)
            {
                logger.LogWarning(
                    "This server serves an older copy of the plugin's endpoints and offers no way to attach the running one's; its buttons answer again after a restart.");

                return false;
            }

            detach.Invoke(registrar, [pluginId]);

            bool attached = attach.Invoke(registrar, [info, manager]) is true;

            logger.LogInformation(
                attached
                    ? "The server was serving an older copy of this plugin's endpoints, so the running copy's were attached."
                    : "The server was serving an older copy of this plugin's endpoints, and attaching the running copy's did not take; its buttons answer again after a restart.");

            return attached;
        }
        catch (Exception wrong) when (wrong is TargetInvocationException or MemberAccessException or ArgumentException or InvalidOperationException)
        {
            logger.LogWarning(wrong, "The plugin's endpoints could not be attached again: {Reason}", wrong.Message);

            return false;
        }
    }

    /// <summary>Whether the server already serves the running copy's controllers.</summary>
    /// <remarks>
    /// Asked by the controller that follows the one that re-attached them: two answers to one press read
    /// the plugin twice, and the second must say the same thing as the first — press again — rather than
    /// that the server needs a restart.
    /// </remarks>
    public bool IsServing(Ulid pluginId)
    {
        try
        {
            return services.GetService(typeof(IPluginManager)) is IPluginManager manager
                   && Find(RegistrarType) is Type type
                   && services.GetService(type) is object registrar
                   && manager.GetPluginInstance(pluginId)?.GetType().Assembly is Assembly running
                   && Method(type, "OwnerOf", 1)?.Invoke(registrar, [running]) is object owner
                   && string.Equals(owner.ToString(), pluginId.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception wrong) when (wrong is TargetInvocationException or MemberAccessException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>A public method by name and by how many parameters it takes.</summary>
    /// <remarks>
    /// Not by the parameters' types: a type this plugin names and the server's own of the same name can come from
    /// two load contexts, and matched by type the method would not be found at all.
    /// </remarks>
    private static MethodInfo? Method(Type type, string name, int parameters)
    {
        return type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(one => one.Name == name && one.GetParameters().Length == parameters);
    }

    /// <summary>A type by name, from whatever the server has loaded.</summary>
    private static Type? Find(string name)
    {
        return AppDomain.CurrentDomain
            .GetAssemblies()
            .Select(one => one.GetType(name, throwOnError: false))
            .FirstOrDefault(one => one is not null);
    }
}

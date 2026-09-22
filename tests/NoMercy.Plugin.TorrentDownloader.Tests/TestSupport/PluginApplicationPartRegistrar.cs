using System.Reflection;
using NoMercy.PluginSdk.Abstractions;

// The server's own name for it, so a container handing it to a stale controller hands over exactly what the
// server's would, and a test can prove the plugin leaves it alone. Only the members a plugin could reach are
// here, with the server's signatures: OwnerOf, Attach and Detach.
namespace NoMercy.Api.Plugins;

/// <summary>Stands in for the media server's registrar of plugin controllers.</summary>
public sealed class PluginApplicationPartRegistrar
{
    /// <summary>Which assembly's controllers are served for each plugin.</summary>
    public Dictionary<Ulid, Assembly> Attached { get; } = [];

    /// <summary>How many times a plugin's controllers were detached.</summary>
    public int Detached { get; private set; }

    public Ulid? OwnerOf(Assembly assembly)
    {
        foreach (KeyValuePair<Ulid, Assembly> entry in Attached)
        {
            if (ReferenceEquals(entry.Value, assembly))
            {
                return entry.Key;
            }
        }

        return null;
    }

    /// <remarks>As the server's does: nothing for a plugin already attached.</remarks>
    public bool Attach(PluginInfo info, IPluginManager pluginManager)
    {
        if (Attached.ContainsKey(info.Id))
        {
            return false;
        }

        if (pluginManager.GetPluginInstance(info.Id)?.GetType().Assembly is not Assembly assembly)
        {
            return false;
        }

        Attached[info.Id] = assembly;

        return true;
    }

    public void Detach(Ulid pluginId)
    {
        if (Attached.Remove(pluginId))
        {
            Detached++;
        }
    }
}

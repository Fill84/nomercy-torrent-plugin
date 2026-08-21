using System.Reflection;

using NoMercy.Plugins.Mvc;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Controllers;

/// <summary>
/// The host can build every controller this plugin serves.
/// </summary>
/// <remarks>
/// <para>
/// ASP.NET Core builds a plugin's controller per request out of the
/// <em>server's</em> container. Nothing this plugin defines is in that
/// container: the plugin instance is created by the loader, not registered as a
/// service, and <c>IPluginServiceRegistrator</c> runs in a discovery pass
/// before any plugin has a context, so it has nothing live to hand out.
/// </para>
/// <para>
/// Both controllers asked for <c>TorrentDownloaderPlugin</c> by constructor.
/// Every request to every endpoint therefore died before reaching a line of
/// this plugin's code:
/// </para>
/// <para>
/// <c>Unable to resolve service for type 'TorrentDownloaderPlugin' while
/// attempting to activate 'SettingsController'.</c>
/// </para>
/// <para>
/// A 500, on Save, on Run, on Stop, on Pause, on Cancel, on Add — every control
/// the plugin offers. <c>IPluginManager</c> is the way to the live plugin: it
/// is a host singleton, and it is the same container the controller comes from.
/// </para>
/// </remarks>
public class ControllersTheHostCanBuildTests
{
    [Fact]
    public void NoControllerAsksTheHostForSomethingOnlyThisPluginHas()
    {
        Assembly mine = typeof(TorrentDownloaderPlugin).Assembly;

        IReadOnlyList<Type> controllers =
        [
            .. mine.GetTypes()
                .Where(type => typeof(PluginControllerBase).IsAssignableFrom(type) && !type.IsAbstract),
        ];

        Assert.NotEmpty(controllers);

        foreach (Type controller in controllers)
        {
            foreach (ConstructorInfo constructor in controller.GetConstructors())
            {
                foreach (ParameterInfo parameter in constructor.GetParameters())
                {
                    Assert.True(
                        parameter.ParameterType.Assembly != mine,
                        $"{controller.Name} asks for {parameter.ParameterType.Name}, which only this "
                        + "plugin defines. The host builds controllers from its own container and "
                        + "this plugin registers nothing in it, so every request to this controller "
                        + "fails with 500 before reaching any of its code. Reach the live plugin "
                        + "through IPluginManager instead.");
                }
            }
        }
    }
}

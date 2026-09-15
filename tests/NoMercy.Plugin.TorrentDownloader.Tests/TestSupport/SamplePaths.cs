using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>A path for a route with every parameter filled in, for tests that walk every page.</summary>
/// <remarks>
/// A route like <c>/shows/:id</c> is not an address anybody asks for; <c>/shows/41</c> is. Walking the
/// declared paths as they are would ask for a show called ":id" and prove nothing about the page.
/// </remarks>
public static class SamplePaths
{
    public const string ShowId = "41";

    public const string LibraryId = "01HQ5W4AVF30N10RT6XCF6AJHM";

    public static string Of(PluginRoute route)
    {
        return route.Build(new Dictionary<string, string>
        {
            ["id"] = route.Path.StartsWith("/shows", StringComparison.Ordinal) ? ShowId : LibraryId,
            ["page"] = "2",
        });
    }
}

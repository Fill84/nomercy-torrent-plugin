using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// Reading a rendered view the way a person would, rather than the way it is
/// built.
/// </summary>
/// <remarks>
/// A test that walks a fixed path into the tree — items[2].items[0] — asserts
/// the layout rather than what the page says, and breaks on a wrapper being
/// added while saying nothing about whether the page still tells the truth.
/// These ask what is on the page.
/// </remarks>
public static class Rendered
{
    /// <summary>Every word the page puts on screen, in order.</summary>
    public static IReadOnlyList<string> Words(PluginView view)
    {
        List<string> found = [];

        // A view with no components at all is a page that says nothing, which
        // is a legitimate thing to assert about rather than to throw over.
        foreach (PluginComponent component in view.Components ?? [])
        {
            Collect(component, found);
        }

        return found;
    }

    /// <summary>Every component in the tree, whatever its depth.</summary>
    public static IEnumerable<PluginComponent> All(PluginView view)
    {
        return (view.Components ?? []).SelectMany(Flatten);
    }

    /// <summary>The one component with this id, or a failure that names it.</summary>
    public static PluginComponent ById(PluginView view, string id)
    {
        return All(view).SingleOrDefault(component => component.Id == id)
               ?? throw new InvalidOperationException(
                   $"No component '{id}'. The page has: {string.Join(", ", All(view).Select(component => component.Id))}");
    }

    private static IEnumerable<PluginComponent> Flatten(PluginComponent component)
    {
        yield return component;

        foreach (PluginComponent child in component.Items.SelectMany(Flatten))
        {
            yield return child;
        }
    }

    private static void Collect(PluginComponent component, List<string> found)
    {
        // "text" is what the one drawable leaf carries; "helperText" is what a
        // caption carries. Both are words a person reads.
        foreach (string key in (string[])["text", "helperText"])
        {
            if (component.Props.TryGetValue(key, out object? value) && value is string word)
            {
                found.Add(word);
            }
        }

        foreach (PluginComponent child in component.Items)
        {
            Collect(child, found);
        }
    }
}

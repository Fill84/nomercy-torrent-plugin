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

    /// <summary>
    /// Every value in the tree, whatever prop it sits under and however deeply
    /// nested.
    /// </summary>
    /// <remarks>
    /// <see cref="Words"/> deliberately reads only what a person is shown, so
    /// it would not notice a secret written into a field's value. This notices.
    /// </remarks>
    public static IReadOnlyList<string> EveryValue(PluginView view)
    {
        List<string> found = [];

        foreach (PluginComponent component in All(view))
        {
            foreach (object? value in component.Props.Values)
            {
                Unpack(value, found);
            }
        }

        return found;
    }

    private static void Unpack(object? value, List<string> found)
    {
        switch (value)
        {
            case null:
                return;
            case string text:
                found.Add(text);
                return;
            // A form's fields are carried whole rather than as nested
            // components, and they are exactly where a secret would end up if
            // one ever escaped into a page. Left to the default below, each one
            // read as its own type name and this helper saw nothing at all.
            case PluginFormField field:
                Unpack(field.Name, found);
                Unpack(field.Label, found);
                Unpack(field.Placeholder, found);
                Unpack(field.Value, found);
                return;
            case IDictionary<string, object?> nested:
                foreach (object? inner in nested.Values)
                {
                    Unpack(inner, found);
                }

                return;
            case System.Collections.IEnumerable list:
                foreach (object? inner in list)
                {
                    Unpack(inner, found);
                }

                return;
            default:
                found.Add(value.ToString() ?? string.Empty);
                return;
        }
    }

    private static void Collect(PluginComponent component, List<string> found)
    {
        // Everything a person is shown or a reader announces: the drawable
        // leaf's text, a caption's helper text, and the names a control carries.
        // Not "value" — that is what a person types, and a page is judged on
        // what it says.
        foreach (string key in (string[])["text", "helperText", "labelText", "label", "ariaLabel", "placeholder"])
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

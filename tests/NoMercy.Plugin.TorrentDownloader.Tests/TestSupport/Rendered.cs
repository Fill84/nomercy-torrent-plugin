using NoMercy.Plugin.TorrentDownloader.Views;
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
        // Everything a person is shown or a reader announces: the words a text
        // node carries, the title and the line under it, a table's empty
        // message, and the names a control carries.
        //
        // "value" is on this list because the client's own text component is
        // where a page's words live — it draws props.value. What a person types
        // is a form field, and those are read by EveryValue rather than here.
        foreach (string key in (string[])
                 [
                     "value", "text", "title", "description", "message", "emptyMessage",
                     "helperText", "labelText", "label", "ariaLabel", "placeholder", "submitLabel",
                 ])
        {
            if (component.Props.TryGetValue(key, out object? value) && value is string word)
            {
                found.Add(word);
            }
        }

        // A form's field labels are drawn beside the boxes, so they are words
        // on the page like any other. Their values are not: what a person typed
        // is read by EveryValue, which is what looks for a secret that escaped.
        if (component.Props.GetValueOrDefault("fields") is IEnumerable<PluginFormField> fields)
        {
            foreach (PluginFormField field in fields)
            {
                found.Add(field.Label);
            }
        }

        // A table row is its cells: the client reads each one straight off the
        // props under the column's key, so every value on a row is something a
        // person is shown and none of the keys is known in advance.
        if (component.Component == Ui.RowComponent)
        {
            foreach (object? cell in component.Props.Values)
            {
                if (cell is not null and not IDictionary<string, object?>)
                {
                    found.Add(cell.ToString() ?? string.Empty);
                }
            }
        }

        foreach (PluginComponent child in component.Items)
        {
            Collect(child, found);
        }
    }
}

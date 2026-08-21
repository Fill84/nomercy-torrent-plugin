using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// A card of fields an owner can actually type into.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PluginViews.Form"/> puts the submit action on the card itself.
/// The client makes anything carrying an action interactive — a tab stop,
/// <c>role="button"</c>, a click handler, and a keydown handler that fires on
/// Enter and on space — so the card becomes a button wrapped around the text
/// boxes. Clicking into a box submits. Space submits instead of typing a space,
/// which means a folder path with a space in it cannot be typed at all. And the
/// card draws a focus ring of its own around the field's.
/// </para>
/// <para>
/// It also hands each input its current text as <c>value</c>, where the design
/// system's input declares a model and reads <c>modelValue</c>, so every box
/// opened empty however much was behind it.
/// </para>
/// <para>
/// So the card is built here: the action goes on the button, where an action
/// belongs, and the value goes where the input looks for it.
/// </para>
/// </remarks>
public static class Forms
{
    public static PluginComponent Section(
        string id,
        string submitLabel,
        PluginActionIntent submit,
        params PluginFormField[] fields)
    {
        return new()
        {
            Id = id,
            Component = PluginComponentType.Form,
            Props = new()
            {
                ["box"] = new Dictionary<string, object?>
                {
                    ["direction"] = "column",
                    ["width"] = "full",
                    ["gap"] = new Dictionary<string, object?> { ["all"] = "3" },
                    ["padding"] = new Dictionary<string, object?> { ["all"] = "4" },
                },
            },
            Items =
            [
                .. fields.Select(field => Field(id, field)),
                PluginViews.Button($"{id}-submit", submitLabel, submit, variant: "primary"),
            ],

            // No action on the card. That is the whole point of this class.
        };
    }

    /// <summary>One labelled control.</summary>
    private static PluginComponent Field(string formId, PluginFormField field)
    {
        string id = $"{formId}-{field.Name}";

        if (field.Type == PluginFormFieldType.Toggle || field.Type == PluginFormFieldType.Checkbox)
        {
            return new()
            {
                Id = id,
                Component = field.Type == PluginFormFieldType.Toggle ? "NMToggle" : "NMCheckbox",
                Props = new()
                {
                    ["name"] = field.Name,
                    ["labelText"] = field.Label,
                    ["checked"] = field.Value as bool? ?? false,
                    ["ariaLabel"] = field.Label,
                },
            };
        }

        return new()
        {
            Id = $"{id}-group",
            Component = PluginComponentType.Container,
            Props = new()
            {
                ["variant"] = "ghost",
                ["box"] = new Dictionary<string, object?>
                {
                    ["direction"] = "column",
                    ["width"] = "full",
                    ["gap"] = new Dictionary<string, object?> { ["all"] = "1" },
                },
            },
            Items =
            [
                new()
                {
                    Id = $"{id}-label",
                    Component = "NMFormLabel",
                    Items = [Words($"{id}-label-text", field.Label)],
                },
                Control(id, field),
            ],
        };
    }

    private static PluginComponent Control(string id, PluginFormField field)
    {
        Dictionary<string, object?> props = new()
        {
            ["name"] = field.Name,
            ["placeholder"] = field.Placeholder ?? field.Label,

            // Where the design system's input and select both look. Handed as
            // "value" it is a prop neither of them declares, so the box drew
            // empty whatever was behind it.
            ["modelValue"] = field.Value?.ToString() ?? string.Empty,
        };

        if (field.Options.Count > 0)
        {
            props["options"] = field.Options;
        }

        return new()
        {
            Id = id,
            Component = field.Type == PluginFormFieldType.Select ? "NMSelect" : "NMInput",
            Props = props,
        };
    }

    /// <summary>
    /// A word in a slot.
    /// </summary>
    /// <remarks>
    /// Every design component takes its content from a slot and a payload can
    /// only put components in a slot, so this is the one leaf that carries
    /// text. A label built without it is an empty label.
    /// </remarks>
    private static PluginComponent Words(string id, string text)
    {
        return new()
        {
            Id = id,
            Component = PluginComponentType.Text,
            Props = new() { ["text"] = text },
        };
    }
}

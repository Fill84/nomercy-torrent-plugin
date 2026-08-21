using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// The components the dashboard actually draws, named the way it names them.
/// </summary>
/// <remarks>
/// <para>
/// This file exists because <see cref="PluginComponentType"/> and the deployed
/// client disagree, and the disagreement is silent. The contract this plugin
/// compiles against maps most of the vocabulary onto one design-system name:
/// </para>
/// <para>
/// <c>Container = List = Row = Grid = Card = Detail = Form = Table = "NMCard"</c>
/// </para>
/// <para>
/// The client keys its own plugin components by their own names —
/// <c>PluginForm</c>, <c>PluginTable</c>, <c>PluginGrid</c> — and resolves a
/// node in two steps: if the name is a design-system component it draws it as
/// one, otherwise it looks in the plugin map. So every node sent as
/// <c>"NMCard"</c> was drawn as a plain design-system card and the plugin
/// component behind that name was never reached.
/// </para>
/// <para>
/// That one mismatch is most of what looked broken on the Settings page:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>PluginForm</c> is a real <c>&lt;form&gt;</c> that collects its fields and
/// posts them under <c>payload.payload</c>. Sent as <c>"NMCard"</c> it became a
/// clickable box with no form in it — so every save arrived as <c>{}</c>, and
/// because the card carried the action it was also a button wrapped around its
/// own text boxes: clicking into one submitted, and space submitted instead of
/// typing a space.
/// </description></item>
/// <item><description>
/// <c>PluginTable</c> draws a table with columns. Sent as <c>"NMCard"</c> it
/// drew a stack of boxes.
/// </description></item>
/// </list>
/// <para>
/// The names and props below were read off the client — <c>types/plugins.ts</c>
/// for the names, each <c>Plugin*.vue</c> for the props it declares — rather
/// than guessed. If the client later moves to the design-system names, this
/// file is the one place that has to change.
/// </para>
/// </remarks>
public static class Ui
{
    public const string ContainerComponent = "PluginContainer";
    public const string TextComponent = "PluginText";
    public const string ListComponent = "PluginList";
    public const string RowComponent = "PluginRow";
    public const string DetailComponent = "PluginDetail";
    public const string ButtonComponent = "PluginButton";
    public const string FormComponent = "PluginForm";
    public const string TableComponent = "PluginTable";
    public const string BadgeComponent = "PluginBadge";
    public const string EmptyStateComponent = "PluginEmptyState";

    public static PluginComponent Text(string id, string value, string? variant = null)
    {
        return new()
        {
            Id = id,
            Component = TextComponent,
            Props = new() { ["value"] = value, ["variant"] = variant },
        };
    }

    /// <summary>Things side by side.</summary>
    public static PluginComponent Row(string id, params PluginComponent[] items)
    {
        return Holder(id, RowComponent, items);
    }

    /// <summary>Things one under the other.</summary>
    public static PluginComponent List(string id, params PluginComponent[] items)
    {
        return Holder(id, ListComponent, items);
    }

    public static PluginComponent Container(string id, params PluginComponent[] items)
    {
        return Holder(id, ContainerComponent, items);
    }

    /// <summary>
    /// One row of a table: a cell per column key.
    /// </summary>
    /// <remarks>
    /// The table reads each cell straight off the row's props under the
    /// column's key, so a row is its cells and nothing else.
    /// </remarks>
    public static PluginComponent Row(
        string id,
        IReadOnlyDictionary<string, object?> cells,
        PluginActionIntent? action = null)
    {
        return new()
        {
            Id = id,
            Component = RowComponent,
            Props = new(cells),
            Action = action,
        };
    }

    public static PluginComponent Table(
        string id,
        IReadOnlyList<PluginTableColumn> columns,
        IReadOnlyList<PluginComponent> rows,
        string? emptyMessage = null)
    {
        return new()
        {
            Id = id,
            Component = TableComponent,
            Props = new()
            {
                ["columns"] = columns,
                ["emptyMessage"] = emptyMessage,
            },
            Items = [.. rows],
        };
    }

    public static PluginComponent Detail(
        string id,
        string title,
        string? description = null,
        string? image = null,
        params PluginComponent[] items)
    {
        return new()
        {
            Id = id,
            Component = DetailComponent,
            Props = new()
            {
                ["title"] = title,
                ["description"] = description,
                ["image"] = image,
            },
            Items = [.. items],
        };
    }

    public static PluginComponent Button(
        string id,
        string label,
        PluginActionIntent action,
        string? icon = null,
        string? variant = null)
    {
        return new()
        {
            Id = id,
            Component = ButtonComponent,
            Props = new()
            {
                ["label"] = label,
                ["icon"] = icon,
                ["variant"] = variant,
            },
            Action = action,
        };
    }

    public static PluginComponent Badge(
        string id,
        string label,
        string variant = PluginBadgeVariant.Neutral)
    {
        return new()
        {
            Id = id,
            Component = BadgeComponent,
            Props = new() { ["label"] = label, ["variant"] = variant },
        };
    }

    public static PluginComponent EmptyState(string id, string title, string? message = null)
    {
        return new()
        {
            Id = id,
            Component = EmptyStateComponent,
            Props = new() { ["title"] = title, ["message"] = message },
        };
    }

    /// <summary>
    /// A real form: its fields are collected when the button is pressed and
    /// sent under <c>payload.payload</c>, which is what makes typing into a
    /// page worth anything at all.
    /// </summary>
    /// <remarks>
    /// The fields are a prop, not children. The client walks the prop to draw
    /// the inputs and to read them back; children it never looks at, so fields
    /// sent as items drew boxes that submitted nothing.
    /// </remarks>
    public static PluginComponent Form(
        string id,
        string submitLabel,
        PluginActionIntent submitAction,
        params PluginFormField[] fields)
    {
        return new()
        {
            Id = id,
            Component = FormComponent,
            Props = new()
            {
                ["submitLabel"] = submitLabel,
                ["fields"] = fields,
            },
            Action = submitAction,
        };
    }

    private static PluginComponent Holder(string id, string component, PluginComponent[] items)
    {
        return new()
        {
            Id = id,
            Component = component,
            Items = [.. items],
        };
    }
}

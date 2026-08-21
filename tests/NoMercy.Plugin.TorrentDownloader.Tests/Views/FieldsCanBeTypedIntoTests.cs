using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>
/// Nothing an owner types into sits inside something that acts on a keypress.
/// </summary>
/// <remarks>
/// <para>
/// The client makes any component carrying an action interactive: it gives it
/// <c>role="button"</c>, a tab stop, a click handler and a keydown handler that
/// fires on Enter and on <em>space</em>.
/// </para>
/// <para>
/// So a card that both holds fields and carries the submit action is a button
/// wrapped around a text box. Clicking into the box submits the form. Pressing
/// space submits it instead of typing a space — a folder path with a space in
/// it cannot be typed at all. And the card takes a focus ring of its own around
/// the field's, which is what it looks like from the outside.
/// </para>
/// <para>
/// The button inside carries the action, which is where an action belongs. The
/// card only holds things.
/// </para>
/// </remarks>
public class FieldsCanBeTypedIntoTests
{
    [Fact]
    public void NoContainerHoldingAFieldAlsoCarriesAnAction()
    {
        foreach ((string page, PluginView view) in EveryPage())
        {
            foreach (PluginComponent component in Rendered.All(view))
            {
                if (!HoldsAField(component))
                {
                    continue;
                }

                Assert.True(
                    component.Action is null,
                    $"'{component.Id}' on the {page} page holds a field and carries an action, so "
                    + "the client draws a button around the box: clicking into it submits, and "
                    + "space submits instead of typing a space.");
            }
        }
    }

    /// <remarks>
    /// The action has to live somewhere, and the button is where a person
    /// expects to press it.
    /// </remarks>
    [Fact]
    public void TheButtonInsideCarriesTheAction()
    {
        PluginView view = SettingsView.Render(new(), [], []);

        PluginComponent submit = Rendered.ById(view, "folders-submit");

        Assert.Equal(PluginComponentType.Button, submit.Component);
        Assert.Equal(
            SettingsView.SaveAction,
            Assert.IsType<PluginActionIntent>(submit.Action).Payload["method"]);
    }

    /// <summary>Whether anything inside this component is typed into.</summary>
    private static bool HoldsAField(PluginComponent component)
    {
        return Everything(component)
            .Any(inner => inner.Component is "NMInput" or "NMSelect" or "NMFileUpload");
    }

    private static IEnumerable<PluginComponent> Everything(PluginComponent component)
    {
        foreach (PluginComponent child in component.Items)
        {
            yield return child;

            foreach (PluginComponent inner in Everything(child))
            {
                yield return inner;
            }
        }
    }

    private static IEnumerable<(string Page, PluginView View)> EveryPage()
    {
        yield return ("Settings", SettingsView.Render(new(), [], []));
        yield return ("Downloads", DownloadsView.Render([]));
    }
}

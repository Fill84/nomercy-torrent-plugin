using System.Reflection;

using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>
/// Every component on every page is one the client draws as a plugin component.
/// </summary>
/// <remarks>
/// <para>
/// The client resolves a node in two steps: a design-system name is drawn as a
/// design-system component, and anything else is looked up in the plugin map.
/// <see cref="PluginComponentType"/> maps most of the vocabulary onto
/// <c>"NMCard"</c> — Container, List, Row, Grid, Card, Detail, Form and Table
/// are all that one string — so a page built with it is a stack of identical
/// cards and the plugin components behind those names are never reached.
/// </para>
/// <para>
/// It fails silently. The page renders, it looks roughly right, and a form is a
/// clickable box that collects nothing: every save arrived as <c>{}</c>, and
/// because the card carried the action it was a button wrapped around its own
/// text boxes.
/// </para>
/// <para>
/// So nothing here may send a design-system name. <see cref="Ui"/> is the
/// vocabulary, read off the client rather than guessed.
/// </para>
/// </remarks>
public class EveryComponentIsOneTheClientDrawsTests
{
    [Fact]
    public async Task NoPageSendsAComponentTheClientDrawsAsAPlainCard()
    {
        IReadOnlyList<string> known = Known();

        Assert.NotEmpty(known);

        using TorrentDownloaderPlugin plugin = new();
        plugin.Initialize(new FakePluginContext());

        foreach (PluginRoute route in plugin.Routes.Routes)
        {
            PluginView page = await plugin.GetViewAsync(
                new() { Route = route.Path },
                CancellationToken.None);

            foreach (PluginComponent component in Rendered.All(page))
            {
                Assert.True(
                    known.Contains(component.Component),
                    $"The page at {route.Path} sends '{component.Component}' for '{component.Id}'. "
                    + "The client has no plugin component under that name and draws it as a plain "
                    + "design-system card, which is how a form becomes a box that saves nothing.");
            }
        }
    }

    /// <summary>Every component name this plugin is allowed to send.</summary>
    private static IReadOnlyList<string> Known()
    {
        return
        [
            .. typeof(Ui)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field is { IsLiteral: true, FieldType.Name: nameof(String) })
                .Select(field => field.GetRawConstantValue())
                .OfType<string>(),
        ];
    }
}

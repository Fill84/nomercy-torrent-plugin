using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc;

using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Mvc;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Controllers;

/// <summary>
/// Every page of the plugin answers at the address the web app asks for it by.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The owner's report of 11 September 2026: every page of the plugin
/// put 404s in the browser's console.</strong> Before the web app draws a page
/// it fetches that page's own address from the server —
/// <c>api/v1/dashboard/plugins/{id}/shows</c> for the Shows page — and nothing
/// answered there. The page itself still arrived, through
/// <c>plugins/{id}/view</c>, so it looked like a page that worked with errors
/// underneath it. The owner's decision was that the plugin answers.
/// </para>
/// <para>
/// The dashboard's own address is the plugin itself, <c>dashboard/plugins/{id}</c>,
/// and the server answers that one; every other page is the plugin's to answer.
/// </para>
/// </remarks>
public class EveryPageTheAppFetchesIsAnsweredTests
{
    [Fact]
    public void EveryPageAnswersAtTheAddressTheWebAppAsksForItBy()
    {
        string[] templates = [.. Templates()];

        string[] pages =
        [
            .. Pages.Routes.Routes
                .Select(route => route.Path)
                .Where(path => path != Pages.DashboardRoute),
        ];

        // A walk that found no page would pass in silence.
        Assert.NotEmpty(pages);

        foreach (string path in pages)
        {
            string asked = $"api/v1/dashboard/plugins/{PluginIdentity.IdText}{path}";

            Assert.True(
                templates.Any(template => Answers(template, asked)),
                $"The web app asks for '{asked}' before it draws the {path} page, and nothing here answers it. "
                + "Answered: " + string.Join(", ", templates));
        }
    }

    /// <summary>Every GET route the plugin's own controllers answer to.</summary>
    private static IEnumerable<string> Templates()
    {
        return typeof(TorrentDownloaderPlugin).Assembly
            .GetTypes()
            .Where(type => typeof(PluginControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .SelectMany(method => method.GetCustomAttributes<HttpGetAttribute>())
            .Select(attribute => attribute.Template)
            .OfType<string>();
    }

    /// <summary>
    /// Whether a route answers an address, the way the server would match it.
    /// </summary>
    /// <remarks>
    /// Only an absolute route can: the server puts every other plugin route
    /// under <c>api/v1/plugins/{id}</c>, which is not where the web app looks.
    /// </remarks>
    private static bool Answers(string template, string address)
    {
        if (!template.StartsWith("~/", StringComparison.Ordinal))
        {
            return false;
        }

        string[] wanted = template[2..].Split('/');
        string[] asked = address.Split('/');

        if (wanted.Length != asked.Length)
        {
            return false;
        }

        for (int at = 0; at < wanted.Length; at++)
        {
            if (!Matches(wanted[at], asked[at]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Matches(string segment, string asked)
    {
        if (segment == "v{version:apiVersion}")
        {
            return asked == "v1";
        }

        if (!segment.StartsWith('{'))
        {
            return string.Equals(segment, asked, StringComparison.OrdinalIgnoreCase);
        }

        int constraint = segment.IndexOf(":regex(", StringComparison.Ordinal);

        if (constraint < 0)
        {
            return true;
        }

        string pattern = segment[(constraint + ":regex(".Length)..segment.LastIndexOf(')')];

        return Regex.IsMatch(asked, pattern);
    }
}

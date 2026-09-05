using System.Globalization;

using Microsoft.AspNetCore.Mvc;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Mvc;

namespace NoMercy.Plugin.TorrentDownloader.Controllers;

/// <summary>What a page is told about the settings.</summary>
/// <param name="Settings">Everything except the secrets.</param>
/// <param name="SecretsSet">
/// The names of the secrets that exist. Names, because a page that could be
/// handed a value could show one.
/// </param>
public sealed record SettingsResponse(Settings Settings, IReadOnlyList<string> SecretsSet);

/// <summary>A secret being set, which is the only direction one ever travels.</summary>
public sealed record SecretRequest(string Key, string Value);

/// <summary>
/// The settings endpoints. The page and these are two ways into one save, so
/// neither of them validates: <see cref="SettingsStore"/> does, once.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IPluginManager"/>, never the plugin itself. ASP.NET Core builds
/// this controller per request out of the <em>server's</em> container, and
/// nothing this plugin defines is in it: the loader creates the plugin, it is
/// registered as no service, and <c>IPluginServiceRegistrator</c> runs in a
/// discovery pass before any plugin has a context, so it has nothing live to
/// give. Asking for the plugin by constructor made every request to every
/// endpoint here fail with a 500 before reaching a line of this code.
/// </para>
/// </remarks>
public sealed class SettingsController(IPluginManager plugins) : PluginControllerBase
{
    /// <summary>The running plugin, or nothing with a reason saying why not.</summary>
    /// <remarks>
    /// The reason matters as much as the refusal: an empty 404 reads exactly
    /// like a route that was never registered. See <see cref="LivePlugin"/>.
    /// </remarks>
    private TorrentDownloaderPlugin? Live => LivePlugin.Of(plugins, PluginId, out _);

    /// <summary>Why the plugin could not be reached, for the answer to carry.</summary>
    private string Unreachable
    {
        get
        {
            _ = LivePlugin.Of(plugins, PluginId, out string refusal);

            return refusal;
        }
    }

    [HttpGet("settings")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (Live is not TorrentDownloaderPlugin plugin)
        {
            return NotFound(Unreachable);
        }

        return Data(new SettingsResponse(
            await plugin.Settings.LoadAsync(ct),
            await plugin.Settings.SecretsSetAsync(ct)));
    }

    [HttpPost("settings")]
    public async Task<IActionResult> Save([FromBody] Settings settings, CancellationToken ct)
    {
        if (Live is not TorrentDownloaderPlugin plugin)
        {
            return NotFound(Unreachable);
        }

        SaveResult result = await plugin.Settings.SaveAsync(settings, ct);

        // Not a 4xx: a refused save is an answer the page renders beside the
        // field, and the reasons are the answer. An error status would have the
        // client showing "something went wrong" over the top of them.
        return Status(result, result.Saved ? "ok" : "refused", result.Said());
    }

    /// <summary>
    /// Applies what the Settings page posted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A form posts flat names and values and nothing else, so this takes them
    /// as they arrive and <see cref="SettingsEdit"/> puts each where it
    /// belongs. Only what was named is touched: the page saves one section at a
    /// time and the sections it did not send must survive the save.
    /// </para>
    /// <para>
    /// Like <see cref="Save"/> it does not validate — the store does, once —
    /// and a refusal is an answer the page renders, not an error status.
    /// </para>
    /// </remarks>
    [HttpPost("settings/edit")]
    public async Task<IActionResult> Edit(
        [FromBody] Dictionary<string, object?> fields,
        CancellationToken ct)
    {
        if (Live is not TorrentDownloaderPlugin plugin)
        {
            return NotFound(Unreachable);
        }

        Settings settings = await plugin.Settings.LoadAsync(ct);

        IReadOnlyList<string> refused = SettingsEdit.Apply(
            settings,
            fields.ToDictionary(field => field.Key, field => Text(field.Value), StringComparer.Ordinal));

        if (refused.Count > 0)
        {
            // Nothing is saved when anything was refused. Saving the fields
            // that landed would leave the owner looking at a page where some of
            // what they typed took and some did not, with no way to tell which.
            return Status(new SaveResult(false, refused, []), "refused", string.Join(" ", refused));
        }

        SaveResult result = await plugin.Settings.SaveAsync(settings, ct);

        return Status(result, result.Saved ? "ok" : "refused", result.Said());
    }

    /// <summary>
    /// A posted value as the text it stands for.
    /// </summary>
    /// <remarks>
    /// JSON carries a number as a number and a tick as a boolean, and the
    /// culture is pinned because a rate typed as 1.5 must not arrive as 15 on a
    /// machine that writes it 1,5.
    /// </remarks>
    private static string? Text(object? value)
    {
        return value switch
        {
            null => null,
            bool flag => flag ? "true" : "false",
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }

    /// <summary>
    /// Stores an API key or a passkey. There is no endpoint that reads one
    /// back: nothing outside the secret store ever needs the value, so nothing
    /// outside it is offered one.
    /// </summary>
    [HttpPost("settings/secrets")]
    public async Task<IActionResult> SetSecret([FromBody] SecretRequest request, CancellationToken ct)
    {
        if (Live is not TorrentDownloaderPlugin plugin)
        {
            return NotFound(Unreachable);
        }

        await plugin.Settings.SetSecretAsync(request.Key, request.Value, ct);

        return Status(request.Key, "ok");
    }

    [HttpDelete("settings/secrets/{key}")]
    public async Task<IActionResult> ForgetSecret(string key, CancellationToken ct)
    {
        if (Live is not TorrentDownloaderPlugin plugin)
        {
            return NotFound(Unreachable);
        }

        await plugin.Settings.ForgetSecretAsync(key, ct);

        return Status(key, "ok");
    }

    /// <summary>
    /// Starts a full cycle in the background.
    /// </summary>
    /// <remarks>
    /// <strong>F1.</strong> The request's own cancellation token is taken and
    /// deliberately not used: 0.3.4 awaited the cycle inside the request, so a
    /// browser tab closed after half an hour threw away twenty-nine minutes of
    /// work. This answers that a cycle has begun, not that it has finished.
    /// </remarks>
    [HttpPost("run")]
    public IActionResult Run(CancellationToken ct)
    {
        _ = ct;

        if (Live is not TorrentDownloaderPlugin plugin)
        {
            return NotFound(Unreachable);
        }

        return plugin.StartRun()
            ? Status(true, "started")
            : Status(false, "already-running", "A cycle is already running.");
    }

    /// <summary>
    /// Cancels the running cycle.
    /// </summary>
    /// <remarks>
    /// Transfers already handed to the torrent client keep going: stopping a
    /// search is not stopping a download.
    /// </remarks>
    [HttpPost("stop")]
    public IActionResult Stop()
    {
        if (Live is not TorrentDownloaderPlugin plugin)
        {
            return NotFound(Unreachable);
        }

        return plugin.StopRun()
            ? Status(true, "stopping")
            : Status(false, "idle", "Nothing is running, so there is nothing to stop.");
    }
}

using Microsoft.AspNetCore.Mvc;
using NoMercy.Plugin.TorrentDownloader.Configuration;
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
public sealed class SettingsController(TorrentDownloaderPlugin plugin) : PluginControllerBase
{
    [HttpGet("settings")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        return Data(new SettingsResponse(
            await plugin.Settings.LoadAsync(ct),
            await plugin.Settings.SecretsSetAsync(ct)));
    }

    [HttpPost("settings")]
    public async Task<IActionResult> Save([FromBody] Settings settings, CancellationToken ct)
    {
        SaveResult result = await plugin.Settings.SaveAsync(settings, ct);

        // Not a 4xx: a refused save is an answer the page renders beside the
        // field, and the reasons are the answer. An error status would have the
        // client showing "something went wrong" over the top of them.
        return Status(
            result,
            result.Saved ? "ok" : "refused",
            result.Saved ? null : string.Join(" ", result.Errors));
    }

    /// <summary>
    /// Stores an API key or a passkey. There is no endpoint that reads one
    /// back: nothing outside the secret store ever needs the value, so nothing
    /// outside it is offered one.
    /// </summary>
    [HttpPost("settings/secrets")]
    public async Task<IActionResult> SetSecret([FromBody] SecretRequest request, CancellationToken ct)
    {
        await plugin.Settings.SetSecretAsync(request.Key, request.Value, ct);

        return Status(request.Key, "ok");
    }

    [HttpDelete("settings/secrets/{key}")]
    public async Task<IActionResult> ForgetSecret(string key, CancellationToken ct)
    {
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
        return plugin.StopRun()
            ? Status(true, "stopping")
            : Status(false, "idle", "Nothing is running, so there is nothing to stop.");
    }
}

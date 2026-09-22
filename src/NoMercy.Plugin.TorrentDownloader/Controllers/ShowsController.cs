using Microsoft.AspNetCore.Mvc;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Mvc;

namespace NoMercy.Plugin.TorrentDownloader.Controllers;

/// <summary>
/// What the overview's row buttons and the two settings forms post.
/// </summary>
/// <remarks>
/// <c>docs/specs/show-list.md</c>. Thin by design: the form's text becomes settings in
/// <see cref="ShowSettingsEdit"/>, and saving is the plugin's. A refused form is an answer the page
/// draws, with the fields named, rather than an error status the client would show over the top of it.
/// Like every controller here it reaches the plugin through <see cref="IPluginManager"/>, never by
/// constructor — see <see cref="SettingsController"/>.
/// </remarks>
public sealed class ShowsController(IPluginManager plugins) : PluginControllerBase
{
    private TorrentDownloaderPlugin? Live => LivePlugin.Of(plugins, PluginId, out _);

    private string Unreachable
    {
        get
        {
            _ = LivePlugin.Of(plugins, PluginId, out string refusal);

            return refusal;
        }
    }

    [HttpPost("shows/{id:int}/on")]
    public Task<IActionResult> On(int id, CancellationToken ct)
    {
        return Switch(id, on: true, ct);
    }

    [HttpPost("shows/{id:int}/off")]
    public Task<IActionResult> Off(int id, CancellationToken ct)
    {
        return Switch(id, on: false, ct);
    }

    /// <summary>
    /// Narrows the overview to the shows whose titles carry what was typed.
    /// </summary>
    /// <remarks>
    /// An endpoint rather than an address, because the web app drops everything after a question mark and
    /// a term in the path would have to be escaped into one. Nothing is saved: it is display state the
    /// plugin holds, exactly as Show advanced is, and the page is drawn again from it.
    /// </remarks>
    [HttpPost("shows/find")]
    public IActionResult Find([FromBody] Dictionary<string, object?> fields)
    {
        if (Live is not TorrentDownloaderPlugin plugin)
        {
            return NotFound(Unreachable);
        }

        plugin.Find(PostedFields.AsText(fields).GetValueOrDefault("find"));

        return Status(true, plugin.Finding is null ? "showing every show" : $"showing what matches '{plugin.Finding}'");
    }

    /// <summary>Puts the whole list back.</summary>
    [HttpPost("shows/find/clear")]
    public IActionResult Clear()
    {
        if (Live is not TorrentDownloaderPlugin plugin)
        {
            return NotFound(Unreachable);
        }

        plugin.Find(null);

        return Status(true, "showing every show");
    }

    [HttpPost("shows/{id:int}/settings")]
    public async Task<IActionResult> Settings(int id, [FromBody] Dictionary<string, object?> fields, CancellationToken ct)
    {
        if (Live is not TorrentDownloaderPlugin plugin)
        {
            return NotFound(Unreachable);
        }

        return Saved(await plugin.SaveShowSettingsAsync(id, PostedFields.AsText(fields), ct));
    }

    [HttpPost("libraries/{id}/preferences")]
    public async Task<IActionResult> Preferences(string id, [FromBody] Dictionary<string, object?> fields, CancellationToken ct)
    {
        if (Live is not TorrentDownloaderPlugin plugin)
        {
            return NotFound(Unreachable);
        }

        return Saved(await plugin.SaveLibraryPreferencesAsync(id, PostedFields.AsText(fields), ct));
    }

    private async Task<IActionResult> Switch(int id, bool on, CancellationToken ct)
    {
        if (Live is not TorrentDownloaderPlugin plugin)
        {
            return NotFound(Unreachable);
        }

        await plugin.SwitchShowAsync(id, on, ct);

        return Status(true, on ? "switched on" : "switched off");
    }

    private IActionResult Saved(IReadOnlyList<string> refused)
    {
        return refused.Count == 0
            ? Status(true, "saved")
            : Status(false, "refused", string.Join(" ", refused));
    }
}

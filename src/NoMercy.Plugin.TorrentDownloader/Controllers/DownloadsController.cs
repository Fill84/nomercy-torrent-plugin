using Microsoft.AspNetCore.Mvc;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Mvc;

namespace NoMercy.Plugin.TorrentDownloader.Controllers;

/// <summary>A torrent the owner found themselves.</summary>
/// <param name="Source">A magnet, or a <c>.torrent</c> by address or by path.</param>
public sealed record AddTorrentRequest(string Source);

/// <summary>One episode to look for now.</summary>
/// <param name="ShowId">Which show.</param>
/// <param name="Season">Which season.</param>
/// <param name="Episode">Which episode.</param>
public sealed record SearchNowRequest(int ShowId, int Season, int Episode);

/// <summary>One release the owner is overruling a refusal for.</summary>
/// <param name="ShowId">Which show.</param>
/// <param name="Season">Which season.</param>
/// <param name="Episode">Which episode.</param>
/// <param name="Title">
/// The release, by the name it was refused under. A release is allowed
/// <em>for</em> an episode: the same file can be wrong for one and right for
/// another.
/// </param>
public sealed record AllowReleaseRequest(int ShowId, int Season, int Episode, string Title);

/// <summary>
/// The controls on the Downloads and Skipped pages.
/// </summary>
/// <remarks>
/// Every one of these is a button an owner presses when something has gone
/// wrong: a download that will not finish, a release the profile refused that
/// they can see is the right one, a torrent they found themselves. None of them
/// answers "ok" to having done nothing — an endpoint that did would leave the
/// owner pressing it again and the page showing something that never happened.
/// </remarks>
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
public sealed class DownloadsController(IPluginManager plugins) : PluginControllerBase
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

    /// <summary>
    /// Looks for one episode now, outside the cadence.
    /// </summary>
    /// <remarks>
    /// <strong>F1.</strong> The request's own token is taken and deliberately
    /// not used: the search belongs to the plugin, and a page closed while it
    /// runs must not throw it away.
    /// </remarks>
    [HttpPost("queue/search")]
    public async Task<IActionResult> Search([FromBody] SearchNowRequest request, CancellationToken ct)
    {

        if (Live is not TorrentDownloaderPlugin plugin)
        {
            return NotFound(Unreachable);
        }
        EpisodeKey episode = new(request.ShowId, request.Season, request.Episode);

        _ = ct;

        return await plugin.StartSearchAsync(episode)
            ? Status(true, "started")
            : Status(false, "unknown", $"Nothing is waiting for {episode}, or a cycle is already running.");
    }

    [HttpPost("downloads/{infoHash}/pause")]
    public async Task<IActionResult> Pause(string infoHash, CancellationToken ct)
    {

        if (Live is not TorrentDownloaderPlugin plugin)
        {
            return NotFound(Unreachable);
        }
        return await plugin.PauseDownloadAsync(infoHash, ct)
            ? Status(true, "paused")
            : Unknown(infoHash);
    }

    [HttpPost("downloads/{infoHash}/resume")]
    public async Task<IActionResult> Resume(string infoHash, CancellationToken ct)
    {

        if (Live is not TorrentDownloaderPlugin plugin)
        {
            return NotFound(Unreachable);
        }
        return await plugin.ResumeDownloadAsync(infoHash, ct)
            ? Status(true, "resumed")
            : Unknown(infoHash);
    }

    /// <summary>
    /// Stops a download, forgets it, and puts its episodes back to missing.
    /// </summary>
    /// <remarks>
    /// All three, or the episode is lost: one that stays marked as grabbed with
    /// nothing downloading is one nothing will ever look for again. It is not
    /// blacklisted — the owner said no to this download, not to this release
    /// for ever.
    /// </remarks>
    [HttpPost("downloads/{infoHash}/cancel")]
    public async Task<IActionResult> Cancel(string infoHash, CancellationToken ct)
    {

        if (Live is not TorrentDownloaderPlugin plugin)
        {
            return NotFound(Unreachable);
        }
        return await plugin.CancelDownloadAsync(infoHash, ct)
            ? Status(true, "cancelled")
            : Unknown(infoHash);
    }

    /// <summary>
    /// Takes on a torrent the owner found themselves.
    /// </summary>
    /// <remarks>
    /// It is written down like any other grab, so the Downloads page shows it
    /// and the transfers cadence stages it when it finishes. One that was taken
    /// and never recorded is a file that arrives and is never put anywhere.
    /// </remarks>
    [HttpPost("downloads")]
    public async Task<IActionResult> Add([FromBody] AddTorrentRequest request, CancellationToken ct)
    {

        if (Live is not TorrentDownloaderPlugin plugin)
        {
            return NotFound(Unreachable);
        }
        (string? InfoHash, string? Refusal) added = await plugin.AddTorrentAsync(request.Source, ct);

        // In the client's own words. "Could not add torrent" tells the owner
        // nothing they can act on, and naming what they pasted tells them which
        // of the things they tried was wrong.
        return added.InfoHash is string hash
            ? Status<string?>(hash, "added")
            : Status<string?>(null, "refused", added.Refusal);
    }

    /// <summary>
    /// Grabs a release the profile or the blacklist had refused.
    /// </summary>
    /// <remarks>
    /// The history line names what it had been refused for, so a page never
    /// silently contradicts an earlier decision: without it the owner reads
    /// "allowed" beside a Skipped page that still says no, with nothing to say
    /// which is right.
    /// </remarks>
    [HttpPost("skipped/allow")]
    public async Task<IActionResult> Allow([FromBody] AllowReleaseRequest request, CancellationToken ct)
    {

        if (Live is not TorrentDownloaderPlugin plugin)
        {
            return NotFound(Unreachable);
        }
        bool allowed = await plugin.AllowReleaseAsync(
            new EpisodeKey(request.ShowId, request.Season, request.Episode),
            request.Title,
            ct);

        return allowed
            ? Status(true, "allowed")
            : Status(false, "unknown", $"Nothing refused '{request.Title}' for that episode.");
    }

    /// <summary>A hash this client is not holding, named so the owner can see which.</summary>
    private OkObjectResult Unknown(string infoHash)
    {
        return Status(false, "unknown", $"This client is not holding {infoHash}.");
    }
}

using Microsoft.AspNetCore.Mvc;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugins.Mvc;

namespace NoMercy.Plugin.TorrentDownloader.Controllers;

/// <summary>A torrent the owner found themselves.</summary>
/// <param name="Source">A magnet, or a <c>.torrent</c> by address or by path.</param>
public sealed record AddTorrentRequest(string Source);

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
public sealed class DownloadsController(TorrentDownloaderPlugin plugin) : PluginControllerBase
{
    [HttpPost("downloads/{infoHash}/pause")]
    public async Task<IActionResult> Pause(string infoHash, CancellationToken ct)
    {
        return await plugin.PauseDownloadAsync(infoHash, ct)
            ? Status(true, "paused")
            : Unknown(infoHash);
    }

    [HttpPost("downloads/{infoHash}/resume")]
    public async Task<IActionResult> Resume(string infoHash, CancellationToken ct)
    {
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

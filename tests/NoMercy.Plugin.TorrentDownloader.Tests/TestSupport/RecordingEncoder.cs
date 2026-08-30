using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// A second implementation of the encode gateway, standing where the one built
/// on the contract will stand.
/// </summary>
/// <remarks>
/// media-server #30 gives plugins <c>IPluginEncoder</c> and #35 gives them the
/// episode's id, and on that day this is the shape of the work: a class beside
/// the reflecting one and a line where the plugin is composed. Nothing in the
/// cadence changes. This is that class, written early and told to answer rather
/// than to encode.
/// </remarks>
public sealed class RecordingEncoder : IEncodeGateway
{
    /// <summary>Everything it was asked for, in order.</summary>
    public List<(string StagedFile, EpisodeKey Episode, Show Show, string? Existing)> Asked { get; } = [];

    /// <summary>Whether it takes what it is asked. False is a server refusing.</summary>
    public bool Takes { get; set; } = true;

    /// <summary>The job it names, or null for a server that cannot name one.</summary>
    public string? JobId { get; set; }

    public Task<EncodeAsk> DispatchAsync(
        string stagedFile,
        EpisodeKey episode,
        Show show,
        string? existing,
        CancellationToken ct)
    {
        Asked.Add((stagedFile, episode, show, existing));

        return Task.FromResult(Takes ? new EncodeAsk(true, JobId) : EncodeAsk.No);
    }
}

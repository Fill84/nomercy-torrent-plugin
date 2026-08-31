using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// The server's own encoder, as the contract offers it.
/// </summary>
/// <remarks>
/// It replaced a fake of <c>IJobDispatcher</c> and the job type that went with
/// it, which is what this plugin had to build for itself before media-server
/// #30 — along with a library repository, a file listing and a service scope,
/// all of them there only so a reflecting gateway had something to reflect on.
/// </remarks>
public sealed class FakeEncoder : IPluginEncoder
{
    private readonly List<(string File, string Library, string? Media, string? Preset)> _asked = [];
    private readonly List<(string File, string Library, string? Media, string? Preset)> _queued = [];

    /// <summary>Everything it was asked to encode, in order.</summary>
    public IReadOnlyList<(string File, string Library, string? Media, string? Preset)> Asked => _asked;

    /// <summary>Why it will not take it, or null to take it.</summary>
    public string? Refusal { get; set; }

    /// <summary>The last ask it took, or null where it took none.</summary>
    /// <remarks>
    /// Taken, not merely asked. A refusal is an ask that queued nothing, and a
    /// double that could not tell them apart would call a refused encode a
    /// dispatched one.
    /// </remarks>
    public (string File, string Library, string? Media, string? Preset)? Job =>
        _queued.Count == 0 ? null : _queued[^1];

    /// <summary>How many encodes it really queued.</summary>
    /// <remarks>
    /// Counted, not just kept: one release with eight grab rows dispatched
    /// eight identical jobs and the last one looked exactly like the first.
    /// </remarks>
    public int Dispatches => _queued.Count;

    public Task<PluginEncodeResult> EncodeAsync(
        string file,
        string libraryId,
        string? mediaId = null,
        string? presetId = null,
        CancellationToken ct = default)
    {
        _asked.Add((file, libraryId, mediaId, presetId));

        if (Refusal is not null)
        {
            return Task.FromResult(PluginEncodeResult.Refused(Refusal));
        }

        _queued.Add((file, libraryId, mediaId, presetId));

        return Task.FromResult(PluginEncodeResult.Queued("01KZGKX2G0966V80H26EKGG5T1"));
    }
}

/// <summary>
/// The server, as far as this plugin can see it.
/// </summary>
/// <remarks>
/// Which is now very little, and that is the point: the plugin asks for exactly
/// one thing by type, and everything else it needs arrives through
/// <c>IPluginContext</c>. This was three hundred lines of imitation server —
/// two library repositories that share a name, a file listing with two
/// overloads, an EF context, a job queue and a scope to resolve them in — all
/// of it there because a reflecting gateway had to be given something to
/// reflect on. It went with the gateway.
/// </remarks>
public sealed class FakeProvider : IServiceProvider
{
    public FakeEncoder Encoder { get; } = new();

    public ActivityJournal Journal { get; } = new();

    public CapturingLogger Log { get; } = new();

    public object? GetService(Type serviceType)
    {
        return serviceType == typeof(IPluginEncoder) ? Encoder : null;
    }
}

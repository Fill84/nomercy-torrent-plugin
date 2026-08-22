using NoMercy.Plugin.TorrentDownloader.Core.Sources;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

/// <summary>
/// Something that can post from inside a page, which answers from a script and
/// remembers what it was asked.
/// </summary>
/// <remarks>
/// The request matters as much as the answer here. A signed request the site
/// would refuse looks, from this side, exactly like a site with nothing to
/// offer — so the test reads the body that went out rather than only what came
/// back.
/// </remarks>
public sealed class RecordingPost(string? answers) : IInPagePost
{
    /// <summary>Where it was asked to post, or null if it never was.</summary>
    public Uri? Url { get; private set; }

    /// <summary>The body that went with it.</summary>
    public string? Body { get; private set; }

    public Task<string?> PostAsync(Uri url, string formBody, CancellationToken ct)
    {
        Url = url;
        Body = formBody;

        return Task.FromResult(answers);
    }
}

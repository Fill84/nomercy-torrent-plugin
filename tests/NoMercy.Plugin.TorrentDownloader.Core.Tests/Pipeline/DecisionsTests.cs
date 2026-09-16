using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// What one run has decided so far.
/// </summary>
/// <remarks>
/// Which episodes something taken has settled, and what was refused and why. <strong>H1:</strong> the real
/// settings and the real judge throughout, never a stand-in chooser.
/// </remarks>
public class DecisionsTests
{
    /// <remarks>
    /// An episode settles itself and nothing else: a release name names one episode
    /// (<c>docs/specs/release-names.md</c>). Settling more would leave the rest of the season unsearched for
    /// the run.
    /// </remarks>
    [Fact]
    public void AnEpisodeTakenSettlesOnlyItself()
    {
        Decisions decisions = new(AtQuality("720p"), Blacklist.None);

        Assert.False(decisions.Settled(Silo(6).Key));

        decisions.Settle(Silo(6));

        Assert.True(decisions.Settled(Silo(6).Key));
        Assert.False(decisions.Settled(Silo(7).Key));
    }

    /// <remarks>
    /// A blacklisted name is refused before any indexer is asked for it. A torrent that failed to download is
    /// worth refusing under the name it is offered by next.
    /// </remarks>
    [Fact]
    public void ABlacklistedNameIsRefused()
    {
        Decisions blacklisted = new(AtQuality("720p"), Blacklist.Of(Blacklist.KeyOf(Single.Original)));
        Decisions clear = new(AtQuality("720p"), Blacklist.None);

        Assert.False(blacklisted.JudgeName(Single, Silo(6)).Accepted);
        Assert.True(clear.JudgeName(Single, Silo(6)).Accepted);
    }

    /// <remarks>
    /// A name the show's settings refuse comes back with the reason, and with no site named in it: nothing
    /// was asked, so no site refused anything. The reason is said on the Activity
    /// page while the run is going and kept nowhere (<c>docs/specs/pages.md</c>), so it has to carry the
    /// whole of what happened on its own.
    /// </remarks>
    [Fact]
    public void ARefusedNameComesBackWithTheReasonInWordsTheOwnerCanActOn()
    {
        Decisions decisions = new(AtQuality("2160p"), Blacklist.None);

        Verdict verdict = decisions.JudgeName(Single, Silo(6));

        Assert.False(verdict.Accepted);
        Assert.Contains("720p is not 2160p", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>A real single episode, off the PreDB capture.</summary>
    private static readonly ReleaseName Single = ReleaseName.Parse(Real(
        "predb.xml",
        "rss",
        "Silo.S03E06.720p.WEB.H264-SYLiX"));

    /// <summary>An episode of the show the release above is for.</summary>
    private static TrackedEpisode Silo(int number)
    {
        return new(
            new("Silo".GetHashCode(StringComparison.Ordinal), 3, number),
            "Silo",
            null,
            LibraryKind.Television,
            null,
            new DateOnly(2026, 8, 1),
            EpisodeState.Missing);
    }

    private static string Real(string fixture, string reader, string name)
    {
        Assert.Contains(name, Capture.Rows(fixture, reader));

        return name;
    }

    /// <summary>Every show at this quality, with codec any and no tags.</summary>
    private static SettingsByShow AtQuality(string quality)
    {
        return SettingsByShow.Every(new() { Quality = quality, Searched = true });
    }
}

using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// A release that was not taken, and why.
/// </summary>
/// <param name="Episode">Which gap it was offered for.</param>
/// <param name="Title">The release name, as it was announced.</param>
/// <param name="Source">
/// Which site it came from, or null when nothing was asked — a name the show's
/// settings refused never reached an indexer, and a page listing a site beside that line
/// would be telling the owner something untrue.
/// </param>
/// <param name="Reason">What to tell the owner, in words they can act on.</param>
public sealed record SkippedRelease(EpisodeKey Episode, string Title, string? Source, string Reason)
{
    /// <summary>The show it was refused for, which the Skipped page names beside the episode.</summary>
    public string? ShowTitle { get; init; }
}

/// <summary>
/// What one run has decided so far: every name refused, with its reason, and every episode taken.
/// </summary>
/// <remarks>
/// A per-run object with state rather than a static rule, because the answers change as the run takes things.
/// It holds the real settings of every show and the real judge. <strong>H1:</strong> every test covering
/// 0.3.4's seeder fault stubbed the profile out with a fake chooser and passed while the plugin took nothing at
/// all. Ranking copies and settling a season by a pack went on 15 September 2026: the winner is
/// <see cref="Winner"/>'s, and a release name names one episode.
/// </remarks>
public sealed class Decisions(SettingsByShow settings, IReadOnlySet<string> blacklisted)
{
    private readonly HashSet<EpisodeKey> _settled = [];
    private readonly List<SkippedRelease> _skipped = [];

    /// <summary>Everything refused this run, with the reason for each, for the Skipped page.</summary>
    /// <remarks>
    /// Kept rather than logged and forgotten: "nothing worth taking" is the sentence that hid a release's worth
    /// of faults for a fortnight.
    /// </remarks>
    public IReadOnlyList<SkippedRelease> Skipped => _skipped;

    /// <summary>Whether something already taken this run answers for this episode.</summary>
    public bool Settled(EpisodeKey episode)
    {
        return _settled.Contains(episode);
    }

    /// <summary>Whether this name is worth searching for, for this episode, by the settings of its show.</summary>
    /// <remarks>Every refusal is recorded on the way out, with no site against it: nothing was asked.</remarks>
    public Verdict JudgeName(ReleaseName name, TrackedEpisode episode)
    {
        Verdict verdict = new NameJudge(SettingsFor(episode)).JudgeName(name, episode, blacklisted);

        if (!verdict.Accepted)
        {
            _skipped.Add(new(episode.Key, name.Original, null, verdict.Reason) { ShowTitle = episode.ShowTitle });
        }

        return verdict;
    }

    /// <summary>The settings of this episode's show.</summary>
    public EffectiveSettings SettingsFor(TrackedEpisode episode)
    {
        return settings.For(episode.Key.ShowId);
    }

    /// <summary>Records that a torrent has been taken for this episode.</summary>
    /// <remarks>
    /// Settled when it is taken and not when it is chosen: a winner the client would not take settles nothing,
    /// and the next torrent in winning order is still worth a turn.
    /// </remarks>
    public void Settle(TrackedEpisode episode)
    {
        _settled.Add(episode.Key);
    }
}

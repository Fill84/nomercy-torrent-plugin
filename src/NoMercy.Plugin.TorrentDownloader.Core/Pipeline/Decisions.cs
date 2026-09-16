using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// What one run has decided so far: which episodes are taken, and whether a name is worth asking for.
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

    /// <summary>Whether something already taken this run answers for this episode.</summary>
    public bool Settled(EpisodeKey episode)
    {
        return _settled.Contains(episode);
    }

    /// <summary>Whether this name is worth searching for, for this episode, by the settings of its show.</summary>
    /// <remarks>
    /// The refusal is said on the Activity page while the run is going and written nowhere
    /// (<c>docs/specs/pages.md</c>, the owner's word of 16 September 2026). Their history held 5,851 of
    /// them, every one a name a show's settings refused before an indexer was asked.
    /// </remarks>
    public Verdict JudgeName(ReleaseName name, TrackedEpisode episode)
    {
        return new NameJudge(SettingsFor(episode)).JudgeName(name, episode, blacklisted);
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

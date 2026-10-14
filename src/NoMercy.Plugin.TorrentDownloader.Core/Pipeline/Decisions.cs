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
/// all. Ranking copies went on 15 September 2026: the winner is <see cref="Winner"/>'s. Settling a season by a
/// pack went that day too and came back on 14 October 2026 (GitHub issue #1): a season with several gaps looks
/// for its pack first, and a pack taken answers for every episode of the season searched for.
/// </remarks>
public sealed class Decisions(SettingsByShow settings, IReadOnlySet<string> blacklisted)
{
    /// <summary>Every episode taken for this run, and the season's pack that answers for it when one does.</summary>
    private readonly Dictionary<EpisodeKey, string?> _settled = [];

    /// <summary>Whether something already taken this run answers for this episode.</summary>
    public bool Settled(EpisodeKey episode)
    {
        return _settled.ContainsKey(episode);
    }

    /// <summary>The pack taken this run that answers for this episode, or null when none does.</summary>
    public string? SettledBy(EpisodeKey episode)
    {
        return _settled.GetValueOrDefault(episode);
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

    /// <summary>Whether this name is worth searching for as the pack of a season, by the settings of its show.</summary>
    /// <param name="name">The release name, parsed.</param>
    /// <param name="latest">The latest-aired episode of the season the pack would cover.</param>
    public Verdict JudgePack(ReleaseName name, TrackedEpisode latest)
    {
        return new NameJudge(SettingsFor(latest)).JudgePack(name, latest, blacklisted);
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
    /// <param name="episode">The episode taken for.</param>
    /// <param name="pack">The season's pack it was taken with, or null for the episode's own release.</param>
    public void Settle(TrackedEpisode episode, string? pack = null)
    {
        _settled[episode.Key] = pack;
    }
}

namespace NoMercy.Plugin.TorrentDownloader.Core.Activity;

/// <summary>Something a run counts, for the stage rows on the dashboard.</summary>
public enum RunCounter
{
    /// <summary>Sites looked at before the run, because they might challenge.</summary>
    SitesLookedAt,

    /// <summary>Of those, the ones that really did.</summary>
    SitesChallenged,

    /// <summary>Challenges cleared in the browser.</summary>
    SitesCleared,

    /// <summary>Challenges that could not be cleared.</summary>
    SitesFailed,

    /// <summary>Episodes in this run's queue.</summary>
    Episodes,

    /// <summary>Episodes the sources have been asked about.</summary>
    EpisodesAsked,

    /// <summary>Release names the sources answered with.</summary>
    NamesFound,

    /// <summary>Release names a show's settings refused before any indexer saw them.</summary>
    NamesRefused,

    /// <summary>Questions put to an indexer.</summary>
    Questions,

    /// <summary>Of those, the ones answered with at least one row.</summary>
    QuestionsAnswered,

    /// <summary>Episodes decided, taken or not.</summary>
    Decided,

    /// <summary>Torrents handed to the client.</summary>
    Taken,
}

/// <summary>
/// How far the running search has got, counted from nought when it began.
/// </summary>
/// <param name="StartedAt">When the run began.</param>
/// <param name="Counts">Every counter that has moved; one that has not is nought.</param>
public sealed record SearchProgress(DateTimeOffset StartedAt, IReadOnlyDictionary<RunCounter, int> Counts)
{
    /// <summary>How many of <paramref name="counter"/> this run has done.</summary>
    public int Count(RunCounter counter)
    {
        return Counts.GetValueOrDefault(counter);
    }
}

/// <summary>
/// One line about what the run did for one episode.
/// </summary>
/// <param name="Stage">Which part of the chain it came from.</param>
/// <param name="Episode">The episode, spelled as every stage spells it.</param>
/// <param name="At">When.</param>
/// <param name="Line">
/// What happened, in words: a source and what it answered, a name refused and
/// why, a question to an indexer and how many rows came back. Never a passkey
/// or an API key.
/// </param>
public sealed record EpisodeNote(ActivityStage Stage, string Episode, DateTimeOffset At, string Line);

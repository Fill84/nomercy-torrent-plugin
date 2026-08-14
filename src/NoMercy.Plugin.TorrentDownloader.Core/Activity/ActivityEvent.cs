namespace NoMercy.Plugin.TorrentDownloader.Core.Activity;

/// <summary>What happened to a subject at a stage.</summary>
public enum ActivityOutcome
{
    /// <summary>Running. The subject is in flight until one of the others follows.</summary>
    Started,

    Finished,

    /// <summary>
    /// Stopped, and why. Clears the in-flight entry exactly as
    /// <see cref="Finished"/> does — a stage that threw is not still running —
    /// but leaves a reason behind, or a subject simply disappears from the page
    /// and the journal says nothing about where it went.
    /// </summary>
    Failed,
}

/// <summary>
/// One thing that happened, at one stage, to one subject.
/// </summary>
/// <param name="Stage">Where in the chain.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Subject">
/// What it happened to, as the owner would name it: an episode, a source, a
/// transfer. It is the key an in-flight entry is cleared by, so the start and
/// the finish of one piece of work must spell it the same way.
/// </param>
/// <param name="At">When, from the journal's clock rather than each caller's.</param>
/// <param name="Detail">
/// Why, when there is anything to add — the reason a stage failed, or what it
/// chose. Never a passkey or an API key: this reaches a page and the log.
/// </param>
public sealed record ActivityEvent(
    ActivityStage Stage,
    ActivityOutcome Outcome,
    string Subject,
    DateTimeOffset At,
    string? Detail = null);

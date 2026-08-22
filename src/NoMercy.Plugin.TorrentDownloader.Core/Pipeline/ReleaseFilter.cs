using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// Whether something is acceptable, and why not when it is not.
/// </summary>
/// <remarks>
/// The reason is not optional. Every page that lists what was refused renders
/// it, and "nothing worth taking" — which is what 0.3.4 said — is the sentence
/// that hid a release's worth of faults for a fortnight.
/// </remarks>
public sealed record Verdict(bool Accepted, string Reason)
{
    public static Verdict Yes { get; } = new(true, "accepted");

    public static Verdict No(string reason)
    {
        return new(false, reason);
    }
}

/// <summary>
/// How a blacklisted title is spelled, wherever it is written down.
/// </summary>
/// <remarks>
/// The table keys on "normalised title, or info hash", and normalised has to
/// mean the same thing on both sides of it or a blacklisted release comes back
/// under the same name spelled with different punctuation.
/// </remarks>
public static class Blacklist
{
    public static string KeyOf(string title)
    {
        return TitleMatcher.Normalised(title);
    }

    /// <summary>Nothing refused, which is what a fresh install has.</summary>
    public static IReadOnlySet<string> None { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>These keys refused, and no others.</summary>
    public static IReadOnlySet<string> Of(params string[] keys)
    {
        return new HashSet<string>(keys, StringComparer.Ordinal);
    }
}

/// <summary>
/// The profile, applied.
/// </summary>
/// <remarks>
/// <para>
/// Twice, and to two different things. Which rule belongs where is
/// docs/04-domain.md § The profile, and the split is the whole of <strong>A1</strong>:
/// a name has no seeders and no size, so those two rules live on the copy and
/// nowhere else. 0.3.4 applied them to names, got nought for every one, and
/// refused everything before an indexer was ever asked.
/// </para>
/// <para>
/// Nothing here reaches out for anything. The blacklist arrives as a set the
/// caller has already read, so this class is a function of its arguments and
/// can be put to a real profile in every test that touches a decision —
/// which is <strong>H1</strong>.
/// </para>
/// </remarks>
public sealed class ReleaseFilter(Profile profile)
{
    /// <summary>
    /// The languages that say a release is not in English.
    /// </summary>
    /// <remarks>
    /// <c>multi</c> and <c>dual audio</c> are not among them: both mean several
    /// audio tracks and normally include the English one. A subtitle claim is
    /// not here either — subtitles do not change what a release sounds like.
    /// </remarks>
    private static readonly string[] NotEnglish =
        ["german", "italian", "french", "vostfr", "spanish", "russian", "polish", "swedish", "japanese"];

    /// <summary>What makes a release English enough for the English-only rule.</summary>
    private static readonly string[] CarriesEnglish = ["english", "multi", "dual audio"];

    /// <summary>
    /// Whether this name is worth putting to an indexer for this episode.
    /// </summary>
    /// <param name="name">The release name, parsed.</param>
    /// <param name="episode">The episode it would answer for.</param>
    /// <param name="blacklisted">
    /// Keys already refused, from <see cref="Blacklist.KeyOf"/>. A set rather
    /// than a store: judging is a decision, not an errand.
    /// </param>
    public Verdict JudgeName(ReleaseName name, TrackedEpisode episode, IReadOnlySet<string> blacklisted)
    {
        if (blacklisted.Contains(Blacklist.KeyOf(name.Original)))
        {
            return Verdict.No($"{name.Original} is blacklisted.");
        }

        if (!TitleMatcher.Matches(name.Title, episode.ShowTitle))
        {
            return Verdict.No($"'{name.Title}' is not a release of {episode.ShowTitle}.");
        }

        if (!Slot(name, episode))
        {
            return Verdict.No($"'{name.Original}' is not {episode.Key}.");
        }

        if (name.IsPack && !profile.AllowSeasonPacks)
        {
            return Verdict.No($"'{name.Original}' is a season pack and packs are not wanted.");
        }

        if (name.Resolution is null)
        {
            // Refused for not saying, which is a different sentence from being
            // refused for being 720p — and the owner can act on the difference.
            return Verdict.No($"'{name.Original}' does not say what resolution it is.");
        }

        if (!string.Equals(name.Resolution, profile.MaximumResolution, StringComparison.OrdinalIgnoreCase))
        {
            // One rung, not a ceiling. A ceiling reads as generous and behaves
            // as a downgrade: the 720p copy is usually posted first and would
            // be taken every time.
            return Verdict.No($"{name.Resolution} is not {profile.MaximumResolution}.");
        }

        // Blank is no codec wanted, exactly as "any" is. The field was empty on
        // the owner's own server, and an empty string is not a codec any
        // release claims - compared against one, every release there is is
        // refused for being the wrong codec, with a reason naming nothing.
        if (profile.Wanted is string wanted)
        {
            if (name.Codec is null)
            {
                return profile.CodecTagRequired
                    ? Verdict.No($"'{name.Original}' does not say which codec it is.")
                    : Verdict.Yes;
            }

            if (!string.Equals(name.Codec, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return Verdict.No($"{name.Codec} is not {wanted}.");
            }
        }

        if (profile.EnglishOnly
            && name.Languages.Any(claimed => NotEnglish.Contains(claimed, StringComparer.OrdinalIgnoreCase))
            && !name.Languages.Any(claimed => CarriesEnglish.Contains(claimed, StringComparer.OrdinalIgnoreCase)))
        {
            return Verdict.No(
                $"'{name.Original}' is in {string.Join(", ", name.Languages)} and English only is on.");
        }

        foreach (string term in profile.ExcludeTerms)
        {
            // Against the whole name rather than a field of it, which is what
            // makes this the rule that refuses a release group as well as a
            // word: the group is part of the name it appears in.
            if (term.Length > 0 && name.Original.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return Verdict.No($"'{name.Original}' carries the excluded term {term}.");
            }
        }

        return Verdict.Yes;
    }

    /// <summary>
    /// Whether this copy is worth taking.
    /// </summary>
    /// <remarks>
    /// Only the two rules that can be true of a copy and cannot be true of a
    /// name, plus the blacklist, which is the one rule that applies to both.
    /// Size is not among them: no setting anywhere gives it bounds, and a
    /// bound nobody wrote down is one this plugin will not invent.
    /// </remarks>
    public Verdict JudgeCopy(ReleaseCopy copy, IReadOnlySet<string> blacklisted)
    {
        if (copy.InfoHash is string hash && blacklisted.Contains(hash))
        {
            return Verdict.No($"{hash} is blacklisted.");
        }

        if (blacklisted.Contains(Blacklist.KeyOf(copy.Title)))
        {
            return Verdict.No($"'{copy.Title}' is blacklisted.");
        }

        // Null is not nought. A site that does not publish a count has not said
        // there is nobody there, and refusing on a number nobody gave is the
        // category error this plugin was rewritten for.
        if (copy.Seeders is int seeders && seeders < profile.MinimumSeeders)
        {
            return Verdict.No(
                $"{seeders} seeders on {copy.Source}, and {profile.MinimumSeeders} are wanted.");
        }

        return Verdict.Yes;
    }

    /// <summary>
    /// Whether this name is a release of this episode at all.
    /// </summary>
    /// <remarks>
    /// The two rules that say <em>which</em> episode a name is for, apart from
    /// the rules that say whether it is worth having. The difference matters
    /// because a search engine answers broadly: asked about Silo S03E08 on
    /// 22 August 2026 a site answered with S03E04 to S03E07 as well, and those
    /// rows were never offered for S03E08 by anybody. Recording them as refused
    /// filled the Skipped page with lines the owner could do nothing about, and
    /// throwing them away lost four episodes the library was missing.
    /// </remarks>
    public static bool IsFor(ReleaseName name, TrackedEpisode episode)
    {
        return TitleMatcher.Matches(name.Title, episode.ShowTitle) && Slot(name, episode);
    }

    /// <summary>Whether this name is for this episode.</summary>
    /// <remarks>
    /// A pack answers for the season it covers. Whether one is worth its bytes
    /// is asked of the gaps, not of the name, and belongs to the stage that
    /// knows how many there are.
    /// </remarks>
    private static bool Slot(ReleaseName name, TrackedEpisode episode)
    {
        if (name.Season == episode.Key.Season && name.Episode == episode.Key.Number)
        {
            return true;
        }

        if (name.IsPack && name.Season == episode.Key.Season)
        {
            return true;
        }

        return episode.Absolute is int absolute && Covers(name, absolute);
    }

    /// <summary>Whether an absolute-numbered name covers this number.</summary>
    /// <remarks>
    /// A batch names a range and answers for every episode in it, which is what
    /// <c>01 ~ 64</c> means on the page it was captured from.
    /// </remarks>
    private static bool Covers(ReleaseName name, int absolute)
    {
        return name.Absolute is int first
               && (first == absolute || (name.LastAbsolute is int last && absolute >= first && absolute <= last));
    }
}

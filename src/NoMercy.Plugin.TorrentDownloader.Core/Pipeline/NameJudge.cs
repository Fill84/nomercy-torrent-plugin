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
/// A release name judged against the settings of its show.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/specs/release-names.md</c> § Choosing the release name, the owner's requirements of
/// 15 September 2026: a release name's resolution is the show's quality, its codec is the show's codec,
/// it carries every must tag and it carries no forbidden tag. The settings are the show's with its
/// library's counted in (<see cref="EffectiveSettings"/>). It replaces the one global profile, and with
/// it English only, the codec-tag switch and the excluded terms: a language is a tag now, set per show.
/// </para>
/// <para>
/// A wish refuses nothing. Wishes decide which names are searched first — <see cref="WishGroups"/> —
/// and a wish no name carries leaves the show downloadable.
/// </para>
/// <para>
/// Nothing here reaches out for anything. The blacklist arrives as a set the caller has already read, so
/// this is a function of its arguments and every test puts it to real settings — <strong>H1</strong>.
/// </para>
/// </remarks>
public sealed class NameJudge(EffectiveSettings settings)
{
    /// <summary>The one language claim English only does not refuse, as <see cref="ReleaseName"/> writes it.</summary>
    private const string English = "english";

    /// <summary>
    /// Whether this name is worth putting to an indexer for this episode.
    /// </summary>
    /// <param name="name">The release name, parsed.</param>
    /// <param name="episode">The episode it would answer for.</param>
    /// <param name="blacklisted">
    /// Keys already refused, from <see cref="Blacklist.KeyOf"/>. A set rather than a store: judging is a
    /// decision, not an errand.
    /// </param>
    public Verdict JudgeName(ReleaseName name, TrackedEpisode episode, IReadOnlySet<string> blacklisted)
    {
        if (blacklisted.Contains(Blacklist.KeyOf(name.Original)))
        {
            return Verdict.No($"{name.Original} is blacklisted.");
        }

        if (TitleMatcher.FileType(name.Original) is string type && !Staging.VideoExtensions.Contains("." + type))
        {
            // Only a video file. The name is a claim rather than the truth — what is really in the torrent
            // is judged again when its metadata arrives — but a name that admits to being an executable is
            // not worth a grab, and on 22 August 2026 one was taken: 1.2 GB of
            // Lioness 2023 S03E02 1080p WEB h264-ETHEL.exe.
            return Verdict.No($"'{name.Original}' is a {type} file and only video files are downloaded.");
        }

        if (!EpisodeNaming.Names(name, episode.ShowTitle, episode.Key))
        {
            // Said two ways, because the owner acts on them differently: another programme, or this one's
            // other episode — or a pack or an absolute-numbered post, which names no one episode.
            return TitleMatcher.Matches(name.Title, episode.ShowTitle)
                ? Verdict.No($"'{name.Original}' is not {episode.Key}.")
                : Verdict.No($"'{name.Title}' is not a release of {episode.ShowTitle}.");
        }

        if (episode.ShowYear is int year
            && TitleMatcher.YearAfter(name.Title, episode.ShowTitle) is int named
            && Math.Abs(named - year) > 1)
        {
            // A name carries a year to say which of two programmes of one title it is, so another year is
            // another programme: the owner's report of 17 September 2026, Dark Matter from 2015 taken for the
            // one from 2024. One year either way, because the library's year is the first air date's and a
            // premiere at the end of a year can be named after the next.
            return Verdict.No($"'{name.Original}' is the {episode.ShowTitle} of {named}, and this one is from {year}.");
        }

        if (settings.Quality is not string quality)
        {
            // show-list.md: a show whose quality is set neither by the show nor by its library has nothing
            // searched. Said, because a refusal for "720p is not" nothing names nothing.
            return Verdict.No($"{episode.ShowTitle} has no quality set, on the show or its library.");
        }

        if (name.Resolution is null)
        {
            // Refused for not saying, which is a different sentence from being refused for being 720p —
            // and the owner can act on the difference.
            return Verdict.No($"'{name.Original}' does not say what resolution it is.");
        }

        if (!string.Equals(name.Resolution, quality, StringComparison.OrdinalIgnoreCase))
        {
            // One rung, not a ceiling. A ceiling reads as generous and behaves as a downgrade: the 720p copy
            // is usually posted first and would be taken every time.
            return Verdict.No($"{name.Resolution} is not {quality}.");
        }

        if (settings.EnglishOnly
            && name.Languages.FirstOrDefault(claim => !string.Equals(claim, English, StringComparison.OrdinalIgnoreCase)) is string foreign)
        {
            // Refused beside an English tag as well: ENG.ITA is the release carrying English and Italian
            // both, and on 22 August 2026 that is how a MULTI release came to be taken for an owner who
            // wanted the plain one. A name claiming no language at all is taken, which is most of them —
            // an English release rarely says so.
            return Verdict.No($"'{name.Original}' is marked {foreign} and English only is on.");
        }

        if (!string.Equals(settings.Codec, LibraryPreferences.AnyCodec, StringComparison.OrdinalIgnoreCase))
        {
            // A name that does not say which codec it is cannot be shown to be the one asked for, and an
            // untagged release is where the unwanted codec hides. With codec "any" none of this is asked.
            if (name.Codec is null)
            {
                return Verdict.No($"'{name.Original}' does not say which codec it is, and {settings.Codec} is asked for.");
            }

            if (!string.Equals(name.Codec, settings.Codec, StringComparison.OrdinalIgnoreCase))
            {
                return Verdict.No($"{name.Codec} is not {settings.Codec}.");
            }
        }

        foreach (string forbidden in settings.Forbidden)
        {
            // Before the musts, so the reason given is the forbidden tag: it is the one that can never be
            // outweighed by anything else the name carries.
            if (Carries(name.Original, forbidden))
            {
                return Verdict.No($"'{name.Original}' carries the forbidden tag {forbidden}.");
            }
        }

        foreach (string must in settings.Musts)
        {
            if (!Carries(name.Original, must))
            {
                return Verdict.No($"'{name.Original}' does not carry the must tag {must}.");
            }
        }

        return Verdict.Yes;
    }

    /// <summary>
    /// Whether a release name carries a tag.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A tag is a word of the name, and never part of a longer word: <c>WEB</c> is in
    /// <c>ATVP.WEB-DL</c> and is not in <c>WEBRip</c> or the group <c>playWEB</c>, and <c>DUAL</c> is not in
    /// <c>INDIVIDUAL</c>. A name is its letters and digits, whatever the site put between them — dots,
    /// spaces, dashes, brackets — and case is set aside, as it is for a title.
    /// </para>
    /// <para>
    /// A tag of more than one word, such as <c>WEB-DL</c> or <c>H.264</c>, is carried when its words stand
    /// next to each other in the name, in its order.
    /// </para>
    /// </remarks>
    public static bool Carries(string name, string tag)
    {
        string[] words = Words(name);
        string[] wanted = Words(tag);

        if (wanted.Length == 0)
        {
            return false;
        }

        for (int at = 0; at + wanted.Length <= words.Length; at++)
        {
            if (words.Skip(at).Take(wanted.Length).SequenceEqual(wanted, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] Words(string text)
    {
        return new string([.. text.Select(character => char.IsLetterOrDigit(character) ? character : ' ')])
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

}

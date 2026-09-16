namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>What applies to one show, with its library's preferences counted in.</summary>
/// <remarks>
/// <c>docs/specs/show-list.md</c> § Library preferences, worked out in one place so that every stage
/// that judges a release name reads the same answer: the show's quality, codec and specials where it
/// set them and the library's where it did not; the tag lists of both together; and a tag the two put
/// in different lists counted only in the show's list.
/// </remarks>
public sealed record EffectiveSettings
{
    public string? Quality { get; init; }

    public string Codec { get; init; } = LibraryPreferences.AnyCodec;

    public bool Specials { get; init; }

    /// <summary>
    /// Whether a release name claiming a language other than English is refused.
    /// </summary>
    /// <remarks>
    /// A name claiming none is taken: most English releases say nothing about language, so refusing the
    /// untagged ones would refuse nearly every release there is.
    /// </remarks>
    public bool EnglishOnly { get; init; }

    public IReadOnlyList<string> Wishes { get; init; } = [];

    public IReadOnlyList<string> Musts { get; init; } = [];

    public IReadOnlyList<string> Forbidden { get; init; } = [];

    /// <summary>Whether this show has anything searched for and downloaded.</summary>
    /// <remarks>
    /// Switched on, with a quality on the show or its library. A show switched on from the overview's row
    /// button is searched with its library's settings straight away — the owner's rule of 16 September
    /// 2026, after seven shows switched on that way searched nothing for want of a Save of a form nobody
    /// had reason to open. One with no quality anywhere searches nothing: there is no resolution to judge
    /// a release name against.
    /// </remarks>
    public bool Searched { get; init; }

    public static EffectiveSettings Of(ShowSettings show, LibraryPreferences library)
    {
        string? quality = show.Quality ?? library.Quality;

        // Every tag the show put in any list, in any case. A library tag the show placed differently is
        // left out of the library's list entirely, so it counts once and where the show put it.
        HashSet<string> shows = new(show.Wishes.Concat(show.Musts).Concat(show.Forbidden), StringComparer.OrdinalIgnoreCase);

        return new()
        {
            Quality = quality,
            Codec = show.Codec ?? library.Codec,
            Specials = show.Specials ?? library.Specials,
            EnglishOnly = show.EnglishOnly ?? library.EnglishOnly,
            Wishes = Joined(library.Wishes, show.Wishes, shows),
            Musts = Joined(library.Musts, show.Musts, shows),
            Forbidden = Joined(library.Forbidden, show.Forbidden, shows),
            Searched = show.SwitchedOn && quality is not null,
        };
    }

    /// <summary>The library's list, less what the show placed itself, then the show's own.</summary>
    private static IReadOnlyList<string> Joined(
        IReadOnlyList<string> library,
        IReadOnlyList<string> show,
        HashSet<string> placedByShow)
    {
        return [.. library.Where(tag => !placedByShow.Contains(tag)).Concat(show).Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}

namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>
/// What the owner set for one show or anime on the overview page.
/// </summary>
/// <remarks>
/// <c>docs/specs/show-list.md</c>. Quality, codec and specials are null while the show follows its
/// library; the tag lists hold only the show's own tags, and the library's are added by
/// <see cref="EffectiveSettings"/>. A show nobody has touched is off, and so searches nothing; switched on,
/// it follows its library in everything the owner has not set on the show itself.
/// </remarks>
/// <param name="ShowId">The server's id for the show.</param>
public sealed record ShowSettings(int ShowId)
{
    public bool SwitchedOn { get; init; }

    public string? Quality { get; init; }

    public string? Codec { get; init; }

    public bool? Specials { get; init; }

    /// <summary>Whether only English releases are taken, or null while the show follows its library.</summary>
    public bool? EnglishOnly { get; init; }

    public IReadOnlyList<string> Wishes { get; init; } = [];

    public IReadOnlyList<string> Musts { get; init; } = [];

    public IReadOnlyList<string> Forbidden { get; init; } = [];
}

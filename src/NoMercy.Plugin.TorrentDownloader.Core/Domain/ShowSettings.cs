namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>
/// What the owner set for one show or anime on the overview page.
/// </summary>
/// <remarks>
/// <c>docs/specs/show-list.md</c>. Quality, codec and specials are null while the show follows its
/// library; the tag lists hold only the show's own tags, and the library's are added by
/// <see cref="EffectiveSettings"/>. A show nobody has touched is off and not saved, and so searches
/// nothing.
/// </remarks>
/// <param name="ShowId">The server's id for the show.</param>
public sealed record ShowSettings(int ShowId)
{
    public bool SwitchedOn { get; init; }

    /// <summary>Whether the owner has saved this show's settings at least once.</summary>
    /// <remarks>
    /// Separate from <see cref="SwitchedOn"/>, because the overview's row button switches a show on
    /// without saving anything, and a show switched on that way has nothing searched.
    /// </remarks>
    public bool Saved { get; init; }

    public string? Quality { get; init; }

    public string? Codec { get; init; }

    public bool? Specials { get; init; }

    public IReadOnlyList<string> Wishes { get; init; } = [];

    public IReadOnlyList<string> Musts { get; init; } = [];

    public IReadOnlyList<string> Forbidden { get; init; } = [];
}

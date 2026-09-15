namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>
/// What every show of one library follows until it sets its own.
/// </summary>
/// <remarks>
/// <c>docs/specs/show-list.md</c> § Library preferences. Quality is null until the owner sets it or the
/// server offers the encoding profile of the library's folders, and a show whose quality is set
/// nowhere searches nothing.
/// </remarks>
/// <param name="LibraryId">The server's id for the library.</param>
public sealed record LibraryPreferences(string LibraryId)
{
    /// <summary>No codec wanted in particular.</summary>
    public const string AnyCodec = "any";

    /// <summary>
    /// The codecs a release name can be read as, and nothing else.
    /// </summary>
    /// <remarks>
    /// The parser reads a codec as a family — <c>x265</c>, <c>H.265</c> and <c>HEVC</c> are one — so these
    /// are every answer it can give. Offered as a list because a codec typed a way the parser never
    /// answers would refuse every release there is, silently.
    /// </remarks>
    public static IReadOnlyList<string> Codecs { get; } = [AnyCodec, "h264", "h265", "xvid", "divx"];

    /// <summary>The resolutions a show or library can ask for.</summary>
    public static IReadOnlyList<string> Qualities { get; } = ["2160p", "1080p", "720p", "480p"];

    public string? Quality { get; init; }

    public string Codec { get; init; } = AnyCodec;

    public bool Specials { get; init; }

    public IReadOnlyList<string> Wishes { get; init; } = [];

    public IReadOnlyList<string> Musts { get; init; } = [];

    public IReadOnlyList<string> Forbidden { get; init; } = [];
}

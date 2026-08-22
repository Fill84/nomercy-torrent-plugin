namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>
/// What the owner will accept. Defaults from docs/04-domain.md § Settings.
/// </summary>
/// <remarks>
/// Applied twice and to two different things: to names before anything is
/// searched for, and to copies once they come back. Which rule applies where is
/// docs/04-domain.md § The profile.
/// </remarks>
public sealed class Profile
{
    /// <summary>No codec wanted in particular, which is the default.</summary>
    public const string AnyCodec = "any";

    /// <summary>
    /// The codecs a release name can be read as, and nothing else.
    /// </summary>
    /// <remarks>
    /// The parser answers a codec as a family rather than as a spelling —
    /// <c>x265</c>, <c>H.265</c>, <c>HEVC</c> and <c>265</c> are one family —
    /// so these four are every answer it can give. Offered as a list because a
    /// box takes anything: a codec spelled a way the parser never answers
    /// refuses every release there is, silently, and the owner is left with an
    /// empty queue and no reason for it.
    /// </remarks>
    public static IReadOnlyList<string> Codecs { get; } = [AnyCodec, "h264", "h265", "xvid", "divx"];

    /// <summary>The rungs of the ladder, highest first.</summary>
    /// <remarks>
    /// One rung, not a ceiling: <c>1080p</c> means 1080p. Offered as a list for
    /// the same reason as the codecs — a rung nothing is posted at refuses
    /// everything.
    /// </remarks>
    public static IReadOnlyList<string> Resolutions { get; } = ["2160p", "1080p", "720p", "480p"];

    /// <summary>Season 0. Off, because a special rarely has an air date worth chasing.</summary>
    public bool IncludeSpecials { get; set; }

    /// <summary>One rung, not a ceiling: 1080p means 1080p, not "1080p or less".</summary>
    public string MaximumResolution { get; set; } = "1080p";

    public string Codec { get; set; } = AnyCodec;

    /// <summary>
    /// Whether a release must name its codec to be accepted. Only consulted
    /// when a codec is wanted — see <see cref="CodecTagRequired"/>.
    /// </summary>
    public bool RequireCodecTag { get; set; } = true;

    public bool EnglishOnly { get; set; } = true;

    public List<string> ExcludeTerms { get; set; } = [];

    /// <summary>Judged on a copy, never on a name: a name has no seeders.</summary>
    public int MinimumSeeders { get; set; } = 2;

    public bool AllowSeasonPacks { get; set; } = true;

    /// <summary>How many gaps in a season before a pack is worth its bytes.</summary>
    public int SeasonPackThreshold { get; set; } = 3;

    /// <summary>How many times an episode is looked for before it goes unavailable.</summary>
    public int MaxSearchAttempts { get; set; } = 3;

    /// <summary>
    /// Whether an untagged release is refused.
    /// </summary>
    /// <remarks>
    /// An untagged release is where the unwanted codec hides, so the rule earns
    /// its place — but only once a codec is named. With no codec wanted it
    /// would refuse most of what the feeds carry, and the owner would see an
    /// empty queue with no reason given.
    /// </remarks>
    public bool CodecTagRequired =>
        RequireCodecTag && Wanted is not null;

    /// <summary>
    /// The codec the owner asked for, or null when they asked for none.
    /// </summary>
    /// <remarks>
    /// Blank is none. The field was empty on the owner's own server on
    /// 22 August 2026, and an empty string is not a codec any release claims —
    /// compared against one, every release there is would be refused for being
    /// the wrong codec, with a reason naming nothing at all.
    /// </remarks>
    public string? Wanted =>
        string.IsNullOrWhiteSpace(Codec) || string.Equals(Codec, AnyCodec, StringComparison.OrdinalIgnoreCase)
            ? null
            : Codec.Trim();
}

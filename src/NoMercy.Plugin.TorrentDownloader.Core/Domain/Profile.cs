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
        RequireCodecTag && !string.Equals(Codec, AnyCodec, StringComparison.OrdinalIgnoreCase);
}

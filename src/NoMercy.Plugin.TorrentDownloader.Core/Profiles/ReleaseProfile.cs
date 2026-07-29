using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public record ReleaseProfile
{
    public required string Name { get; init; }
    public required QualityLadder Quality { get; init; }
    public LanguageProfile Language { get; init; } = LanguageProfile.EnglishOnly;
    public VideoCodec Codec { get; init; } = VideoCodec.Unknown;
    public IReadOnlyList<string> BlockedGroups { get; init; } = [];
    public IReadOnlyList<GroupPreference> PreferredGroups { get; init; } = [];
    public IReadOnlyList<TermRule> Terms { get; init; } = [];
    public long? MinSizeBytes { get; init; }
    public long? MaxSizeBytes { get; init; }
    public int MinSeeders { get; init; }
    public bool AllowSeasonPacks { get; init; }
}

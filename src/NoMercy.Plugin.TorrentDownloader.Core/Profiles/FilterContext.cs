using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public record FilterContext(
    string ShowName,
    EpisodeSlot? WantedSlot,
    ReleaseProfile Profile,
    IReadOnlySet<string> BlacklistedNormalisedTitles,
    IReadOnlySet<string> BlacklistedInfoHashes
);

using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

public record SearchQuery(string ShowName, EpisodeSlot? Slot = null)
{
    public string Text => Slot is EpisodeSlot slot ? $"{ShowName} {slot}" : ShowName;
}

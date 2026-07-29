namespace NoMercy.Plugin.TorrentDownloader.Core.Releases;

public readonly record struct EpisodeSlot(int Season, int Episode)
{
    public override string ToString() => $"S{Season:00}E{Episode:00}";
}

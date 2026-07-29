namespace NoMercy.Plugin.TorrentDownloader.Core.Releases;

public enum Resolution
{
    Unknown = 0,
    Sd480 = 1,
    Sd576 = 2,
    Hd720 = 3,
    Fhd1080 = 4,
    Uhd2160 = 5,
}

public enum ReleaseSource
{
    Unknown = 0,
    Cam = 1,
    Telesync = 2,
    DvdRip = 3,
    Hdtv = 4,
    WebRip = 5,
    WebDl = 6,
    BluRay = 7,
    Remux = 8,
}

public readonly record struct Quality(Resolution Resolution, ReleaseSource Source)
{
    public override string ToString() => $"{Resolution}/{Source}";
}

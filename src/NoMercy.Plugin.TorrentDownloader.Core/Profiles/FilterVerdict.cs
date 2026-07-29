namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public record FilterVerdict(bool Accepted, string Reason)
{
    public static FilterVerdict Accept() => new(true, "match");

    public static FilterVerdict Reject(string reason) => new(false, reason);
}

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public enum TermKind
{
    Required = 0,
    Forbidden = 1,
    Preferred = 2,
}

public record TermRule(string Pattern, TermKind Kind, int Score);

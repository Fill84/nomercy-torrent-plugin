namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public record LanguageProfile(
    IReadOnlyList<string> Required,
    IReadOnlyList<string> Preferred,
    IReadOnlyList<string> Forbidden,
    bool RequireDualAudio
)
{
    public static LanguageProfile EnglishOnly { get; } = new(["English"], [], [], false);
}

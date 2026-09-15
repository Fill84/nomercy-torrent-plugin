using System.Text.Json;

namespace NoMercy.Plugin.TorrentDownloader.Storage;

/// <summary>A list of the owner's tags, as the JSON array a settings column holds.</summary>
/// <remarks>
/// JSON rather than a comma-separated string, because a tag is any word a release name can carry and
/// nothing stops the owner typing one with a comma in its neighbourhood; an array never has to guess
/// where one tag ends.
/// </remarks>
internal static class TagList
{
    public static string Write(IReadOnlyList<string> tags)
    {
        return JsonSerializer.Serialize(tags);
    }

    public static IReadOnlyList<string> Read(string column)
    {
        return JsonSerializer.Deserialize<string[]>(column) ?? [];
    }
}

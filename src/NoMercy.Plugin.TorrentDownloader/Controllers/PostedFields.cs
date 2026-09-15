using System.Globalization;

namespace NoMercy.Plugin.TorrentDownloader.Controllers;

/// <summary>What a plugin form posted, as the text each value stands for.</summary>
/// <remarks>
/// JSON carries a number as a number and a tick as a boolean, and the culture is pinned because a rate
/// typed as 1.5 must not arrive as 15 on a machine that writes it 1,5. Every form of this plugin posts
/// through here, so each is read the same way.
/// </remarks>
internal static class PostedFields
{
    public static Dictionary<string, string?> AsText(Dictionary<string, object?> fields)
    {
        return fields.ToDictionary(field => field.Key, field => Text(field.Value), StringComparer.Ordinal);
    }

    public static string? Text(object? value)
    {
        return value switch
        {
            null => null,
            bool flag => flag ? "true" : "false",
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }
}

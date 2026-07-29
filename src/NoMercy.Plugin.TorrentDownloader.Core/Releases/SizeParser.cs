using System.Globalization;
using System.Text.RegularExpressions;

namespace NoMercy.Plugin.TorrentDownloader.Core.Releases;

public static partial class SizeParser
{
    private static readonly Dictionary<string, long> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["B"] = 1L,
        ["KB"] = 1024L,
        ["MB"] = 1024L * 1024L,
        ["GB"] = 1024L * 1024L * 1024L,
        ["TB"] = 1024L * 1024L * 1024L * 1024L,
    };

    [GeneratedRegex(@"([\d.,]+)\s*(TB|GB|MB|KB|B)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SizePattern();

    public static long Parse(string? text)
    {
        Match match = SizePattern().Match(text ?? string.Empty);
        if (!match.Success)
            return 0L;

        string number = match.Groups[1].Value.Replace(",", string.Empty);
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            return 0L;

        return (long)(value * Units[match.Groups[2].Value]);
    }
}

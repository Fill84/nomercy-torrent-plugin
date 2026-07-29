using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers.Parsing;

public static partial class TorznabResultParser
{
    private static readonly XNamespace Torznab = "http://torznab.com/schemas/2015/feed";

    [GeneratedRegex(
        @"btih:([0-9a-f]{40}|[0-9a-z]{32})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex InfoHashPattern();

    public static IReadOnlyList<ReleaseInfo> Parse(string xml, string indexerName, int priority)
    {
        XDocument document;

        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException error)
        {
            throw new IndexerException($"{indexerName}: malformed Torznab XML: {error.Message}", error);
        }

        if (document.Root?.Name.LocalName == "error")
            throw new IndexerException(
                $"{indexerName}: Torznab error {(string?)document.Root.Attribute("code")}: "
                    + (string?)document.Root.Attribute("description")
            );

        return document.Descendants("item").Select(item => ToRelease(item, indexerName, priority)).ToArray();
    }

    private static ReleaseInfo ToRelease(XElement item, string indexerName, int priority)
    {
        string? link = ((string?)item.Element("link"))?.Trim();
        bool isMagnet = link?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true;

        int seeders = Attr(item, "seeders");
        int peers = Attr(item, "peers");
        string? infoHash = AttrText(item, "infohash");

        if (infoHash is null && isMagnet)
        {
            Match match = InfoHashPattern().Match(link!);
            infoHash = match.Success ? match.Groups[1].Value : null;
        }

        return new ReleaseInfo
        {
            IndexerName = indexerName,
            TorrentId = ((string?)item.Element("guid"))?.Trim() ?? link ?? string.Empty,
            Title = ((string?)item.Element("title"))?.Trim() ?? string.Empty,
            DetailUrl = ((string?)item.Element("comments"))?.Trim(),
            MagnetUri = isMagnet ? link : null,
            DownloadUrl = isMagnet ? null : link,
            InfoHash = infoHash?.ToLowerInvariant(),
            SizeBytes = Long(item.Element("size")),
            Seeders = seeders,
            Leechers = Math.Max(peers - seeders, 0),
            IndexerPriority = priority,
            PublishedAt = Date(item.Element("pubDate")),
        };
    }

    private static string? AttrText(XElement item, string name) =>
        item.Elements(Torznab + "attr")
            .FirstOrDefault(attr =>
                string.Equals((string?)attr.Attribute("name"), name, StringComparison.OrdinalIgnoreCase)
            )
            ?.Attribute("value")
            ?.Value;

    private static int Attr(XElement item, string name) =>
        int.TryParse(
            AttrText(item, name),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int value
        )
            ? value
            : 0;

    private static long Long(XElement? element) =>
        long.TryParse(
            (string?)element,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out long value
        )
            ? value
            : 0L;

    private static DateTimeOffset? Date(XElement? element) =>
        DateTimeOffset.TryParse(
            (string?)element,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset value
        )
            ? value
            : null;
}

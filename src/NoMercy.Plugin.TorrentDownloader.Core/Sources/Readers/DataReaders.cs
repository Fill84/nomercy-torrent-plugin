using System.Text.Json;
using System.Text.RegularExpressions;

namespace NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;

/// <summary>
/// A feed in RSS: names and nothing else.
/// </summary>
/// <remarks>
/// PreDB, srrDB and SceneSource all answer this shape. They say what a release
/// is <em>called</em>, which is the point — only a full release name is fit to
/// put to an indexer, and an episode whose gaps rolled out of every feed months
/// ago is found no other way.
/// </remarks>
public sealed class RssNameReader : ISourceReader
{
    private static readonly Regex Items = new(
        "<item>(.*?)</item>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex TitleTag = new(
        "<title>(.*?)</title>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex LinkTag = new(
        "<link>(.*?)</link>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public string Name => "rss";

    public IReadOnlyList<SourceRow> Read(string body, Uri from)
    {
        List<SourceRow> rows = [];

        foreach (Match item in Items.Matches(body))
        {
            Match title = TitleTag.Match(item.Groups[1].Value);

            if (!title.Success)
            {
                continue;
            }

            Match link = LinkTag.Match(item.Groups[1].Value);

            rows.Add(new(
                // Decoded, because srrDB writes every dash in a release name as
                // an entity and a scene name is mostly dashes.
                Html.Text(title.Groups[1].Value),
                link.Success ? Html.Absolute(Html.Text(link.Groups[1].Value), from) : null));
        }

        return rows;
    }
}

/// <summary>
/// Nyaa: an indexer in XML, where every item links a real torrent.
/// </summary>
/// <remarks>
/// For anime it is often the only source that has the release. It answers zero
/// honestly for anything that is not anime, and zero is an answer rather than a
/// fault.
/// </remarks>
public sealed class TorrentRssReader : ISourceReader
{
    private static readonly Regex Items = new(
        "<item>(.*?)</item>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex Tag = new(
        "<(?:[a-z]+:)?(title|link|seeders|leechers|infoHash|size)>(.*?)</(?:[a-z]+:)?\\1>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public string Name => "torrent-rss";

    public IReadOnlyList<SourceRow> Read(string body, Uri from)
    {
        List<SourceRow> rows = [];

        foreach (Match item in Items.Matches(body))
        {
            Dictionary<string, string> tags = Tag.Matches(item.Groups[1].Value)
                .GroupBy(tag => tag.Groups[1].Value.ToLowerInvariant())
                .ToDictionary(tag => tag.Key, tag => Html.Text(tag.First().Groups[2].Value));

            if (!tags.TryGetValue("title", out string? title))
            {
                continue;
            }

            rows.Add(new(
                title,
                Html.Absolute(tags.GetValueOrDefault("link"), from),
                InfoHash: tags.GetValueOrDefault("infohash")?.ToUpperInvariant(),
                Seeders: Html.Count(tags.GetValueOrDefault("seeders")),
                Leechers: Html.Count(tags.GetValueOrDefault("leechers")),
                SizeBytes: Html.Size(tags.GetValueOrDefault("size"))));
        }

        return rows;
    }
}

/// <summary>
/// The Pirate Bay's own endpoint: JSON, with an info hash and honest seeder
/// counts per row.
/// </summary>
/// <remarks>
/// The website is a JavaScript shell with no results in it; this is what it
/// calls. It answers "nothing found" as a single row saying so rather than as
/// an empty array, and that row is not a release.
/// </remarks>
public sealed class ApibayReader : ISourceReader
{
    /// <summary>What it says instead of answering nothing.</summary>
    public const string NothingFound = "No results returned";

    public string Name => "apibay";

    public IReadOnlyList<SourceRow> Read(string body, Uri from)
    {
        List<SourceRow> rows = [];

        foreach (JsonElement row in Json.Array(body))
        {
            string? name = Json.Text(row, "name");

            // Its way of saying nothing. Taking it would put a release called
            // "No results returned" into the pool.
            if (name is null || name.Equals(NothingFound, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? hash = Json.Text(row, "info_hash");

            rows.Add(new(
                name,
                InfoHash: hash?.ToUpperInvariant(),
                Seeders: Json.Number(row, "seeders"),
                Leechers: Json.Number(row, "leechers"),
                SizeBytes: Json.Long(row, "size")));
        }

        return rows;
    }
}

/// <summary>EZTV's own endpoint: JSON, and it publishes the magnet outright.</summary>
public sealed class EztvApiReader : ISourceReader
{
    public string Name => "eztv-api";

    public IReadOnlyList<SourceRow> Read(string body, Uri from)
    {
        List<SourceRow> rows = [];

        foreach (JsonElement row in Json.ArrayUnder(body, "torrents"))
        {
            string? filename = Json.Text(row, "filename") ?? Json.Text(row, "title");

            if (filename is null)
            {
                continue;
            }

            rows.Add(new(
                filename,
                Html.Absolute(Json.Text(row, "episode_url"), from),
                Json.Text(row, "magnet_url"),
                Json.Text(row, "hash")?.ToUpperInvariant(),
                Json.Number(row, "seeds"),
                Json.Number(row, "peers"),
                Json.Long(row, "size_bytes")));
        }

        return rows;
    }
}

/// <summary>
/// srrDB's search: names, not torrents, and that is the point.
/// </summary>
/// <remarks>
/// It says what an episode from three weeks ago is actually called, and only a
/// name like that is fit to put to an indexer. A show with no scene releases
/// honestly answers zero — <c>resultsCount</c> of nought with no results — and
/// that is an answer, not a broken reader.
/// </remarks>
public sealed class SrrdbReader : ISourceReader
{
    public string Name => "srrdb";

    public IReadOnlyList<SourceRow> Read(string body, Uri from)
    {
        return
        [
            .. Json.ArrayUnder(body, "results")
                .Select(row => Json.Text(row, "release"))
                .Where(release => release is not null)
                .Select(release => new SourceRow(release!, SizeBytes: null)),
        ];
    }
}

/// <summary>
/// A Torznab indexer the owner added.
/// </summary>
/// <remarks>
/// The standard shape an indexer manager answers, so one entry in the settings
/// reaches whatever the owner already runs. Its API key never appears here: it
/// is in the address the fetch was given, and every failure that names an
/// address blanks it.
/// </remarks>
public sealed class TorznabReader : ISourceReader
{
    private static readonly Regex Items = new(
        "<item>(.*?)</item>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex TitleTag = new(
        "<title>(.*?)</title>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex Attribute = new(
        @"<torznab:attr\s+name=""([^""]+)""\s+value=""([^""]*)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Enclosure = new(
        @"<enclosure[^>]*url=""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Name => "torznab";

    public IReadOnlyList<SourceRow> Read(string body, Uri from)
    {
        List<SourceRow> rows = [];

        foreach (Match item in Items.Matches(body))
        {
            string markup = item.Groups[1].Value;
            Match title = TitleTag.Match(markup);

            if (!title.Success)
            {
                continue;
            }

            Dictionary<string, string> attributes = Attribute.Matches(markup)
                .GroupBy(attribute => attribute.Groups[1].Value.ToLowerInvariant())
                .ToDictionary(attribute => attribute.Key, attribute => attribute.First().Groups[2].Value);

            Match enclosure = Enclosure.Match(markup);
            string? url = enclosure.Success ? Html.Decode(enclosure.Groups[1].Value) : null;

            rows.Add(new(
                Html.Text(title.Groups[1].Value),
                Html.Absolute(Json.LinkOf(markup), from),
                url?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true ? url : Html.Magnet(markup),
                attributes.GetValueOrDefault("infohash")?.ToUpperInvariant(),
                Html.Count(attributes.GetValueOrDefault("seeders")),
                Html.Count(attributes.GetValueOrDefault("peers")),
                long.TryParse(attributes.GetValueOrDefault("size"), out long size) ? size : Html.Size(markup)));
        }

        return rows;
    }
}

/// <summary>Reading the shapes a JSON endpoint answers in.</summary>
internal static class Json
{
    private static readonly Regex LinkTag = new(
        "<link>(.*?)</link>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public static string? LinkOf(string markup)
    {
        Match found = LinkTag.Match(markup);

        return found.Success ? Html.Text(found.Groups[1].Value) : null;
    }

    /// <summary>The rows of a body that is an array.</summary>
    public static IEnumerable<JsonElement> Array(string body)
    {
        return Parse(body) is JsonElement root && root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : [];
    }

    /// <summary>The rows of an array under one property of an object.</summary>
    public static IEnumerable<JsonElement> ArrayUnder(string body, string property)
    {
        return Parse(body) is JsonElement root
               && root.ValueKind == JsonValueKind.Object
               && root.TryGetProperty(property, out JsonElement array)
               && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
            : [];
    }

    public static string? Text(JsonElement row, string property)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(property, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    /// <summary>
    /// A count, whether the endpoint writes it as a number or as a string.
    /// </summary>
    /// <remarks>
    /// apibay writes every one of them as a string and EZTV writes them as
    /// numbers, so insisting on either shape loses one of the two.
    /// </remarks>
    public static int? Number(JsonElement row, string property)
    {
        return int.TryParse(Text(row, property), out int number) ? number : null;
    }

    public static long? Long(JsonElement row, string property)
    {
        return long.TryParse(Text(row, property), out long number) ? number : null;
    }

    private static JsonElement? Parse(string body)
    {
        try
        {
            return JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException)
        {
            // A body that is not JSON is a source that answered something else
            // — a challenge page, an error, an outage notice. No rows is the
            // honest reading, and the health tool is what says so out loud.
            return null;
        }
    }
}

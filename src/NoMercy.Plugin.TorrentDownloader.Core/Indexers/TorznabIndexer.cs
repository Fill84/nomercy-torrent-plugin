using System.Globalization;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers.Parsing;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

public sealed class TorznabIndexer(
    string name,
    int priority,
    Uri baseUrl,
    string apiKey,
    HttpClient http,
    IReadOnlyList<int>? categories = null
) : IIndexer
{
    public string Name => name;

    public int Priority => priority;

    public async Task<IReadOnlyList<ReleaseInfo>> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        Uri url = BuildUrl(query);
        HttpResponseMessage response;

        try
        {
            response = await http.GetAsync(url, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            throw new IndexerException($"{name}: search request failed: {error.Message}", error);
        }

        if (!response.IsSuccessStatusCode)
            throw new IndexerException($"{name}: search returned HTTP {(int)response.StatusCode}");

        // Buffered by GetAsync's default completion option — see RssIndexer.FetchAsync.
        string body = await response.Content.ReadAsStringAsync(ct);
        return TorznabResultParser.Parse(body, name, priority);
    }

    private Uri BuildUrl(SearchQuery query)
    {
        List<string> parameters =
        [
            $"t={(query.Slot is null ? "search" : "tvsearch")}",
            $"apikey={EscapeQueryValue(apiKey)}",
            $"q={EscapeQueryValue(query.ShowName)}",
        ];

        if (query.Slot is EpisodeSlot slot)
        {
            parameters.Add($"season={slot.Season.ToString(CultureInfo.InvariantCulture)}");
            parameters.Add($"ep={slot.Episode.ToString(CultureInfo.InvariantCulture)}");
        }

        if (categories is { Count: > 0 })
            parameters.Add(
                "cat="
                    + string.Join(",", categories.Select(c => c.ToString(CultureInfo.InvariantCulture)))
            );

        return new Uri($"{baseUrl.ToString().TrimEnd('/')}?{string.Join("&", parameters)}");
    }

    // Uri.EscapeDataString encodes a space as %20, but Uri.ToString() unescapes %20 back to a
    // literal space when the escaped Uri is later stringified (AbsoluteUri keeps it escaped;
    // ToString() does not). That round-trip would put a raw space back into the request URL, so
    // spaces are encoded as '+' instead, which is the conventional query-string form and is not
    // touched by that unescaping.
    private static string EscapeQueryValue(string value) =>
        Uri.EscapeDataString(value).Replace("%20", "+");
}

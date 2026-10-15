using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;

namespace NoMercy.Plugin.TorrentDownloader.Configuration;

/// <summary>
/// Reads the shipped catalogue, which the build writes into the assembly from
/// <c>sources.json</c>.
/// </summary>
/// <remarks>
/// <strong>C1.</strong> 0.3.4 looked for <c>sources.json</c> in
/// <c>AppContext.BaseDirectory</c> — for a plugin loaded into the media server's
/// process, the <em>server's</em> folder — never found it, fell back to three
/// compiled-in sources, and ran for a day looking perfectly healthy while
/// fourteen sources simply did not exist. Reading it from the assembly's own
/// folder fixed that, but needed <c>Assembly.Location</c>, which the marketplace
/// scan refuses (media-server #476). Inside the assembly there is no folder left
/// to get wrong.
/// </remarks>
public sealed class CatalogueLoader(ILogger logger)
{
    /// <summary>
    /// The lowest number of sources a healthy catalogue has.
    /// </summary>
    /// <remarks>
    /// Seventeen ship. Ten is the line below which something has gone wrong
    /// with the file rather than with a site, and it is what a test asserts —
    /// the point being that "three" must never again pass for a catalogue.
    /// </remarks>
    public const int Fewest = 10;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The shipped sources, or an empty list and a loud complaint.</summary>
    public IReadOnlyList<SourceDefinition> Load()
    {
        return Read(ShippedFiles.Catalogue);
    }

    /// <summary>
    /// The sources in <paramref name="catalogue"/>, or an empty list and a loud
    /// complaint.
    /// </summary>
    /// <remarks>
    /// Empty, never a smaller built-in set. A partial fallback is what made
    /// C1 invisible: the plugin went on working, just with fourteen sources
    /// missing, and nothing anywhere said so. Nothing is the honest answer,
    /// and it is the one the owner will notice.
    /// </remarks>
    public IReadOnlyList<SourceDefinition> Read(string catalogue)
    {
        try
        {
            CatalogueFile? file = JsonSerializer.Deserialize<CatalogueFile>(catalogue, Json);

            return file?.Sources is null ? [] : [.. file.Sources.Select(Convert)];
        }
        catch (JsonException exception)
        {
            logger.LogError(
                exception,
                "The shipped sources.json is not readable as a catalogue. The plugin has no sources until it is corrected.");

            return [];
        }
    }

    private static SourceDefinition Convert(SourceEntry entry)
    {
        return new(
            entry.Name,
            entry.Kind,
            entry.Url,
            entry.SearchUrl,
            entry.Reader,
            entry.Query ?? QueryStyles.Words,
            entry.Gated,
            entry.SearchGated,
            entry.Priority,
            entry.MinimumIntervalSeconds,
            entry.Enabled,
            entry.Note)
        {
            Libraries = entry.Libraries ?? [],
            PageParameter = entry.PageParameter,
            Pages = entry.Pages,
            FirstChoice = entry.FirstChoice,
        };
    }

    private sealed class CatalogueFile
    {
        [JsonPropertyName("sources")]
        public List<SourceEntry>? Sources { get; init; }
    }

    private sealed class SourceEntry
    {
        public string Name { get; init; } = string.Empty;

        public string Kind { get; init; } = string.Empty;

        public string Url { get; init; } = string.Empty;

        public string? SearchUrl { get; init; }

        public string? Reader { get; init; }

        public string? Query { get; init; }

        public bool Gated { get; init; }

        public bool SearchGated { get; init; }

        public int Priority { get; init; }

        public int MinimumIntervalSeconds { get; init; } = 15;

        /// <summary>Which libraries it is worth asking, or nothing for all of them.</summary>
        public List<string>? Libraries { get; init; }

        /// <summary>The parameter that asks a listing for its next page, when it has one.</summary>
        public string? PageParameter { get; init; }

        /// <summary>How many pages of one search to read. One unless the file says more.</summary>
        public int Pages { get; init; } = 1;

        /// <summary>This indexer's rank among the first-choice indexers, or nothing for every other.</summary>
        public int? FirstChoice { get; init; }

        /// <summary>On unless the file says otherwise, so a new entry works.</summary>
        public bool Enabled { get; init; } = true;

        public string? Note { get; init; }
    }
}

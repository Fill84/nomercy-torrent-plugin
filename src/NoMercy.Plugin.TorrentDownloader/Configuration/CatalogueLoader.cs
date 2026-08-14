using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;

namespace NoMercy.Plugin.TorrentDownloader.Configuration;

/// <summary>
/// Reads <c>sources.json</c> from the folder the assembly is actually in.
/// </summary>
/// <remarks>
/// <strong>C1.</strong> Not <c>AppContext.BaseDirectory</c>. A plugin is loaded
/// into the media server's process, so that property names the <em>server's</em>
/// folder: 0.3.4 looked there, never found the file, fell back to three
/// compiled-in sources, and ran for a day looking perfectly healthy while
/// fourteen sources simply did not exist.
/// </remarks>
public sealed class CatalogueLoader(ILogger logger)
{
    public const string FileName = "sources.json";

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

    /// <summary>The folder the running assembly sits in — the plugin's own.</summary>
    public static string AssemblyFolder =>
        Path.GetDirectoryName(typeof(CatalogueLoader).Assembly.Location) is string folder && folder.Length > 0
            ? folder
            // A single-file or in-memory assembly has no location. Nothing this
            // plugin ships is loaded that way, and guessing the server's folder
            // is the exact fault this class exists to avoid.
            : throw new InvalidOperationException("This assembly has no folder to read sources.json from.");

    /// <summary>
    /// The shipped sources, or an empty list and a loud complaint.
    /// </summary>
    /// <remarks>
    /// Empty, never a smaller built-in set. A partial fallback is what made
    /// C1 invisible: the plugin went on working, just with fourteen sources
    /// missing, and nothing anywhere said so. Nothing is the honest answer,
    /// and it is the one the owner will notice.
    /// </remarks>
    public IReadOnlyList<SourceDefinition> Load(string? folder = null)
    {
        string path = Path.Combine(folder ?? AssemblyFolder, FileName);

        if (!File.Exists(path))
        {
            logger.LogError(
                "No {FileName} beside the assembly at {Path}. The plugin ships with none of its sources and will find nothing until it is put back.",
                FileName,
                path);

            return [];
        }

        try
        {
            CatalogueFile? file = JsonSerializer.Deserialize<CatalogueFile>(File.ReadAllText(path), Json);

            return file?.Sources is null ? [] : [.. file.Sources.Select(Convert)];
        }
        catch (JsonException exception)
        {
            logger.LogError(
                exception,
                "{Path} is not readable as a catalogue. The plugin has no sources until it is corrected.",
                path);

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
            entry.Note);
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

        /// <summary>On unless the file says otherwise, so a new entry works.</summary>
        public bool Enabled { get; init; } = true;

        public string? Note { get; init; }
    }
}

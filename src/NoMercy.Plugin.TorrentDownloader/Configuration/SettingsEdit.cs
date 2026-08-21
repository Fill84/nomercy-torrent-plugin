using System.Globalization;

using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Configuration;

/// <summary>
/// What the Settings page posts, put where it belongs.
/// </summary>
/// <remarks>
/// <para>
/// A form posts what its fields hold and nothing else: flat names, string
/// values, no structure. The settings are nested, so something has to put one
/// into the other.
/// </para>
/// <para>
/// It applies and it refuses; it does not validate. <see cref="SettingsStore"/>
/// does that, once, so the page and the JSON endpoint cannot come to different
/// conclusions about the same save.
/// </para>
/// <para>
/// Every field is named here rather than found by reflection. A reflected
/// setter would accept whatever a caller posted, including the ones that are
/// nobody's business to set from a page — the learned tracker list, the stored
/// secrets — and the list of what a page may change would exist nowhere.
/// </para>
/// </remarks>
public static class SettingsEdit
{
    private delegate void Setter(Settings settings, string value);

    private delegate string Getter(Settings settings);

    private sealed record Field(Getter Read, Setter Write);

    /// <summary>Every field the Settings page may change, in the page's order.</summary>
    public static IReadOnlyList<string> Fields => [.. Known.Keys];

    /// <summary>What a field holds now, or null if there is no such field.</summary>
    public static string? Read(Settings settings, string name)
    {
        return Known.TryGetValue(name, out Field? field) ? field.Read(settings) : null;
    }

    /// <summary>
    /// Applies every named field, and says what it could not.
    /// </summary>
    /// <returns>
    /// One line per field refused, empty when everything was applied. A field
    /// that fails leaves its setting exactly as it was.
    /// </returns>
    public static IReadOnlyList<string> Apply(
        Settings settings,
        IReadOnlyDictionary<string, string?> fields)
    {
        List<string> problems = [];

        foreach ((string name, string? value) in fields)
        {
            if (!Known.TryGetValue(name, out Field? field))
            {
                // Named, never ignored. A field silently skipped is one the
                // owner filled in, watched save, and believes took effect.
                problems.Add($"There is no setting called '{name}'.");
                continue;
            }

            try
            {
                field.Write(settings, value ?? string.Empty);
            }
            catch (Exception problem) when (problem is FormatException or ArgumentException or OverflowException)
            {
                problems.Add($"'{value}' is not something '{name}' can hold.");
            }
        }

        return problems;
    }

    private static readonly Dictionary<string, Field> Known = new(StringComparer.Ordinal)
    {
        ["incompleteFolder"] = new(
            settings => settings.IncompleteFolder,
            (settings, value) => settings.IncompleteFolder = value.Trim()),
        ["intakeFolder"] = new(
            settings => settings.IntakeFolder,
            (settings, value) => settings.IntakeFolder = value.Trim()),
        ["dryRun"] = new(
            settings => Text(settings.DryRun),
            (settings, value) => settings.DryRun = Flag(value)),

        ["cadences.transfers"] = new(
            settings => settings.Cadences.Transfers,
            (settings, value) => settings.Cadences.Transfers = value.Trim()),
        ["cadences.feed"] = new(
            settings => settings.Cadences.Feed,
            (settings, value) => settings.Cadences.Feed = value.Trim()),
        ["cadences.search"] = new(
            settings => settings.Cadences.Search,
            (settings, value) => settings.Cadences.Search = value.Trim()),
        ["cadences.maintenance"] = new(
            settings => settings.Cadences.Maintenance,
            (settings, value) => settings.Cadences.Maintenance = value.Trim()),

        ["profile.maximumResolution"] = new(
            settings => settings.Profile.MaximumResolution,
            (settings, value) => settings.Profile.MaximumResolution = value.Trim()),
        ["profile.codec"] = new(
            settings => settings.Profile.Codec,
            (settings, value) => settings.Profile.Codec = value.Trim()),
        ["profile.requireCodecTag"] = new(
            settings => Text(settings.Profile.RequireCodecTag),
            (settings, value) => settings.Profile.RequireCodecTag = Flag(value)),
        ["profile.englishOnly"] = new(
            settings => Text(settings.Profile.EnglishOnly),
            (settings, value) => settings.Profile.EnglishOnly = Flag(value)),
        ["profile.includeSpecials"] = new(
            settings => Text(settings.Profile.IncludeSpecials),
            (settings, value) => settings.Profile.IncludeSpecials = Flag(value)),
        ["profile.minimumSeeders"] = new(
            settings => Text(settings.Profile.MinimumSeeders),
            (settings, value) => settings.Profile.MinimumSeeders = Whole(value)),
        ["profile.allowSeasonPacks"] = new(
            settings => Text(settings.Profile.AllowSeasonPacks),
            (settings, value) => settings.Profile.AllowSeasonPacks = Flag(value)),
        ["profile.seasonPackThreshold"] = new(
            settings => Text(settings.Profile.SeasonPackThreshold),
            (settings, value) => settings.Profile.SeasonPackThreshold = Whole(value)),
        ["profile.maxSearchAttempts"] = new(
            settings => Text(settings.Profile.MaxSearchAttempts),
            (settings, value) => settings.Profile.MaxSearchAttempts = Whole(value)),
        ["profile.excludeTerms"] = new(
            settings => string.Join(", ", settings.Profile.ExcludeTerms),
            (settings, value) => settings.Profile.ExcludeTerms = Line(value)),

        ["client.listenPort"] = new(
            settings => Text(settings.Client.ListenPort),
            (settings, value) => settings.Client.ListenPort = Whole(value)),
        ["client.portMapping"] = new(
            settings => Text(settings.Client.PortMapping),
            (settings, value) => settings.Client.PortMapping = Flag(value)),
        ["client.maxDownloadRate"] = new(
            settings => Text(settings.Client.MaxDownloadRate),
            (settings, value) => settings.Client.MaxDownloadRate = Long(value)),
        ["client.maxUploadRate"] = new(
            settings => Text(settings.Client.MaxUploadRate),
            (settings, value) => settings.Client.MaxUploadRate = Long(value)),
        ["client.seedRatio"] = new(
            settings => settings.Client.SeedRatio.ToString(CultureInfo.InvariantCulture),
            (settings, value) => settings.Client.SeedRatio = Fraction(value)),
        ["client.seedHours"] = new(
            settings => Text(settings.Client.SeedHours),
            (settings, value) => settings.Client.SeedHours = Whole(value)),
        ["client.stallMinutes"] = new(
            settings => Text(settings.Client.StallMinutes),
            (settings, value) => settings.Client.StallMinutes = Whole(value)),
        ["client.metadataTimeoutMinutes"] = new(
            settings => Text(settings.Client.MetadataTimeoutMinutes),
            (settings, value) => settings.Client.MetadataTimeoutMinutes = Whole(value)),
        ["client.maxConcurrentDownloads"] = new(
            settings => Text(settings.Client.MaxConcurrentDownloads),
            (settings, value) => settings.Client.MaxConcurrentDownloads = Whole(value)),
        ["client.encryption"] = new(
            settings => settings.Client.Encryption.ToString(),
            (settings, value) => settings.Client.Encryption = Policy(value)),
    };

    /// <summary>
    /// A tick, however the thing that sent it spells one.
    /// </summary>
    /// <remarks>
    /// A checkbox posts "on" in a plain form, "true" from this design system,
    /// and "1" from a script. All three mean the owner ticked the box.
    /// </remarks>
    private static bool Flag(string value)
    {
        return value.Trim().ToLowerInvariant() is "true" or "on" or "1" or "yes";
    }

    private static int Whole(string value)
    {
        return int.Parse(value.Trim(), CultureInfo.InvariantCulture);
    }

    private static long Long(string value)
    {
        return long.Parse(value.Trim(), CultureInfo.InvariantCulture);
    }

    private static double Fraction(string value)
    {
        return double.Parse(value.Trim(), CultureInfo.InvariantCulture);
    }

    private static EncryptionPolicy Policy(string value)
    {
        return Enum.Parse<EncryptionPolicy>(value.Trim(), ignoreCase: true);
    }

    /// <summary>
    /// A list typed on one line, which is how a text field takes one.
    /// </summary>
    /// <remarks>
    /// Empties are dropped rather than kept: a trailing comma is how a person
    /// types a list, and an empty forbidden term would match every release
    /// there is.
    /// </remarks>
    private static List<string> Line(string value)
    {
        return
        [
            .. value
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        ];
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(bool value) => value ? "true" : "false";
}

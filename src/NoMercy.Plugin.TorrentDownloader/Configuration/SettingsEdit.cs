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

        foreach ((string name, string? value) in fields.Where(field => field.Key.StartsWith(SourcePrefix, StringComparison.Ordinal)))
        {
            // One switch per shipped source, so the names cannot be listed here
            // the way every other setting is: they are the catalogue's, and the
            // catalogue is a file that ships beside the assembly. The prefix is
            // the contract instead.
            //
            // Off is the thing recorded. The list holds what the owner turned
            // off, so a source nobody has touched is absent from it - which is
            // what makes every source on by default, including one added by a
            // later version that no stored list could have known about.
            string source = name[SourcePrefix.Length..];

            if (Flag(value ?? string.Empty))
            {
                settings.DisabledDefaultSources.RemoveAll(one =>
                    string.Equals(one, source, StringComparison.OrdinalIgnoreCase));
            }
            else if (!settings.DisabledDefaultSources.Any(one =>
                         string.Equals(one, source, StringComparison.OrdinalIgnoreCase)))
            {
                settings.DisabledDefaultSources.Add(source);
            }
        }

        foreach (string name in fields.Keys.Where(name =>
                     !Known.ContainsKey(name) && !name.StartsWith(SourcePrefix, StringComparison.Ordinal)))
        {
            // Named, never ignored. A field silently skipped is one the owner
            // filled in, watched save, and believes took effect.
            problems.Add($"There is no setting called '{name}'.");
        }

        // In a decided order, not the order they happened to arrive in. A speed
        // is drawn as a preset list and a box beside it, so a form posts both
        // keys for the same setting — and whichever was written last won, which
        // in a dictionary is whatever the caller built it in. An override is
        // applied after the preset it overrides, always.
        foreach ((string name, Field field) in Known.Where(known => fields.ContainsKey(known.Key))
                     .OrderBy(known => Overrides(known.Key) ? 1 : 0)
                     .Select(known => (known.Key, known.Value)))
        {
            string? value = fields[name];

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

        // One, where there were four. Transfers, feed, search and maintenance
        // are not four schedules: they are the steps of one cycle, each started
        // by the last one finishing, and the only question left to ask is how
        // often to start one when nobody has.
        ["cadences.cycle"] = new(
            settings => settings.Cadences.Cycle,
            (settings, value) => settings.Cadences.Cycle = value.Trim()),

        ["client.listenPort"] = new(
            settings => Text(settings.Client.ListenPort),
            (settings, value) => settings.Client.ListenPort = Whole(value)),
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
        ["client.resumeIntervalSeconds"] = new(
            settings => Text(settings.Client.ResumeIntervalSeconds),
            (settings, value) => settings.Client.ResumeIntervalSeconds = Whole(value)),

        // The boxes beside the preset lists, in megabytes a second because that
        // is the unit on the label. Declared after the byte keys and applied
        // after them, so what the owner typed wins over what the list offered.
        ["client.maxDownloadRateMb"] = new(
            settings => string.Empty,
            (settings, value) => settings.Client.MaxDownloadRate =
                Megabytes(value) ?? settings.Client.MaxDownloadRate),
        ["client.maxUploadRateMb"] = new(
            settings => string.Empty,
            (settings, value) => settings.Client.MaxUploadRate =
                Megabytes(value) ?? settings.Client.MaxUploadRate),
    };

    /// <summary>What a switch for one shipped source is called.</summary>
    /// <remarks>
    /// A prefix rather than a name in <c>Known</c>: there is one per entry of a
    /// catalogue that ships as a file, so the set is not known at compile time.
    /// </remarks>
    public const string SourcePrefix = "source.";

    /// <summary>Whether a field overrides another and must therefore be applied after it.</summary>
    private static bool Overrides(string name)
    {
        return name.EndsWith("Mb", StringComparison.Ordinal);
    }

    /// <summary>
    /// Megabytes a second as bytes a second, or null where nothing was typed.
    /// </summary>
    /// <remarks>
    /// Blank is not nought. Nought is unlimited and the preset list is what says
    /// it; an empty box means the owner left the box alone and the preset is
    /// the answer.
    /// </remarks>
    private static long? Megabytes(string value)
    {
        string typed = value.Trim();

        if (typed.Length == 0)
        {
            return null;
        }

        return (long)(double.Parse(typed, CultureInfo.InvariantCulture) * 1024 * 1024);
    }

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

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}

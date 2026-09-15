using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Configuration;

/// <summary>
/// What the settings form of a show or a library posts, put into its settings.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/specs/show-list.md</c> § The settings form. A form posts flat names and text values and
/// nothing else, so each is read here: the empty choice of a select is "follow the library", each tag
/// list arrives as comma-separated text beside a field for one tag appended to its end.
/// </para>
/// <para>
/// <strong>All or nothing.</strong> A quality or codec the page does not offer is refused with the
/// field named, and then nothing of the post is taken — saving the half that landed would leave the
/// owner looking at a form where some of what they typed took and some did not.
/// </para>
/// </remarks>
public static class ShowSettingsEdit
{
    /// <summary>The value of a select that follows the library, and of an empty text box.</summary>
    public const string Follow = "";

    public const string SwitchedOnField = "switchedOn";
    public const string QualityField = "quality";
    public const string CodecField = "codec";
    public const string SpecialsField = "specials";
    public const string WishesField = "wishes";
    public const string MustsField = "musts";
    public const string ForbiddenField = "forbidden";

    /// <summary>The suffix of the field that appends one tag to a list.</summary>
    public const string AddSuffix = ".add";

    /// <summary>The specials select's own values beside following the library.</summary>
    public const string On = "on";
    public const string Off = "off";

    public static (ShowSettings Settings, IReadOnlyList<string> Refused) Show(
        ShowSettings current,
        IReadOnlyDictionary<string, string?> fields)
    {
        List<string> refused = [];

        string? quality = Choice(fields, QualityField, current.Quality, LibraryPreferences.Qualities, follows: true, refused);
        string? codec = Choice(fields, CodecField, current.Codec, LibraryPreferences.Codecs, follows: true, refused);

        if (refused.Count > 0)
        {
            return (current, refused);
        }

        return (current with
        {
            SwitchedOn = fields.TryGetValue(SwitchedOnField, out string? on) ? Flag(on) : current.SwitchedOn,
            Quality = quality,
            Codec = codec,
            Specials = fields.TryGetValue(SpecialsField, out string? specials) ? Specials(specials) : current.Specials,
            Wishes = Tags(fields, WishesField, current.Wishes),
            Musts = Tags(fields, MustsField, current.Musts),
            Forbidden = Tags(fields, ForbiddenField, current.Forbidden),
        }, refused);
    }

    public static (LibraryPreferences Preferences, IReadOnlyList<string> Refused) Library(
        LibraryPreferences current,
        IReadOnlyDictionary<string, string?> fields)
    {
        List<string> refused = [];

        // A library's quality may be left unset — nothing is searched for its shows until it is — but it
        // follows nothing, so the empty choice is "not set" rather than "follow".
        string? quality = Choice(fields, QualityField, current.Quality, LibraryPreferences.Qualities, follows: true, refused);
        string? codec = Choice(fields, CodecField, current.Codec, LibraryPreferences.Codecs, follows: false, refused);

        if (refused.Count > 0)
        {
            return (current, refused);
        }

        return (current with
        {
            Quality = quality,
            Codec = codec ?? current.Codec,
            Specials = fields.TryGetValue(SpecialsField, out string? specials) ? Flag(specials) : current.Specials,
            Wishes = Tags(fields, WishesField, current.Wishes),
            Musts = Tags(fields, MustsField, current.Musts),
            Forbidden = Tags(fields, ForbiddenField, current.Forbidden),
        }, refused);
    }

    /// <summary>
    /// A select's value: one the page offers, the empty choice where that is allowed, or the current
    /// value when the field was not posted.
    /// </summary>
    private static string? Choice(
        IReadOnlyDictionary<string, string?> fields,
        string name,
        string? current,
        IReadOnlyList<string> offered,
        bool follows,
        List<string> refused)
    {
        if (!fields.TryGetValue(name, out string? posted))
        {
            return current;
        }

        string value = (posted ?? Follow).Trim();

        if (value.Length == 0 && follows)
        {
            return null;
        }

        string? known = offered.FirstOrDefault(one => string.Equals(one, value, StringComparison.OrdinalIgnoreCase));

        if (known is null)
        {
            refused.Add($"The {name} '{value}' is not one the page offers: {string.Join(", ", offered)}.");
        }

        return known;
    }

    /// <summary>A tag list from its comma-separated text, with the one-tag field appended to its end.</summary>
    /// <remarks>
    /// A tag already in the list is not added a second time, whatever its case: a tag is matched against a
    /// release name without regard to case, so the second copy would change nothing but the page.
    /// </remarks>
    private static IReadOnlyList<string> Tags(
        IReadOnlyDictionary<string, string?> fields,
        string name,
        IReadOnlyList<string> current)
    {
        bool listed = fields.TryGetValue(name, out string? list);
        bool added = fields.TryGetValue(name + AddSuffix, out string? extra);

        if (!listed && !added)
        {
            return current;
        }

        IEnumerable<string> tags = listed ? Split(list) : current;

        return [.. tags.Concat(Split(extra)).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static IEnumerable<string> Split(string? text)
    {
        return (text ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool? Specials(string? value)
    {
        string choice = (value ?? Follow).Trim();

        return choice.Length == 0 ? null : Flag(choice);
    }

    private static bool Flag(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() is "true" or "on" or "1" or "yes";
    }
}

using System.Globalization;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// The settings form of one show or anime, at <c>/shows/:id</c>.
/// </summary>
/// <remarks>
/// <c>docs/specs/show-list.md</c> § The settings form: one form and one Save, quality, codec and specials
/// each able to follow the library, a comma list and a one-tag field for each tag list, and below the
/// form what applies with the library counted in — so the owner sees the effect of "follow" without
/// opening the library.
/// </remarks>
public static class ShowSettingsView
{
    public const string FormId = "show-settings";

    public const string AppliedId = "applied";

    public static PluginView Render(Show show, ShowSettings settings, LibraryPreferences library)
    {
        EffectiveSettings applied = EffectiveSettings.Of(settings, library);
        string id = show.Id.ToString(CultureInfo.InvariantCulture);

        return new()
        {
            Layout = PluginLayout.Wide,
            Components =
            [
                Ui.Text("show-title", show.Year is int year ? $"{show.Title} ({year})" : show.Title, "title"),
                Ui.Text("show-library", show.LibraryName, "caption"),
                Ui.Form(
                    FormId,
                    "Save",
                    PluginActionIntent.CallPlugin($"shows/{id}/settings", null, PluginActionTransport.Rest),
                    [
                        new PluginFormField
                        {
                            Name = ShowSettingsEdit.SwitchedOnField,
                            Label = "Switched on",
                            Type = PluginFormFieldType.Toggle,
                            Value = settings.SwitchedOn,
                        },
                        Select(ShowSettingsEdit.QualityField, "Quality", settings.Quality, library.Quality ?? "not set", LibraryPreferences.Qualities),
                        Select(ShowSettingsEdit.CodecField, "Codec", settings.Codec, library.Codec, LibraryPreferences.Codecs),
                        new PluginFormField
                        {
                            Name = ShowSettingsEdit.SpecialsField,
                            Label = "Specials (season 0)",
                            Type = PluginFormFieldType.Select,
                            Value = settings.Specials switch
                            {
                                true => ShowSettingsEdit.On,
                                false => ShowSettingsEdit.Off,
                                null => ShowSettingsEdit.Follow,
                            },
                            Options =
                            [
                                Follows(library.Specials ? "on" : "off"),
                                new() { Value = ShowSettingsEdit.On, Label = "on" },
                                new() { Value = ShowSettingsEdit.Off, Label = "off" },
                            ],
                        },
                        new PluginFormField
                        {
                            Name = ShowSettingsEdit.EnglishOnlyField,
                            Label = "English only",
                            Type = PluginFormFieldType.Select,
                            Value = settings.EnglishOnly switch
                            {
                                true => ShowSettingsEdit.On,
                                false => ShowSettingsEdit.Off,
                                null => ShowSettingsEdit.Follow,
                            },
                            Options =
                            [
                                Follows(library.EnglishOnly ? "on" : "off"),
                                new() { Value = ShowSettingsEdit.On, Label = "on" },
                                new() { Value = ShowSettingsEdit.Off, Label = "off" },
                            ],
                        },
                        .. TagFields(settings.Wishes, settings.Musts, settings.Forbidden),
                    ]),
                Ui.Text(
                    AppliedId,
                    "What applies: " + string.Join(
                        " · ",
                        applied.Quality ?? "no quality, so nothing is searched",
                        applied.Codec,
                        applied.Specials ? "specials on" : "specials off",
                        applied.EnglishOnly ? "English only on" : "English only off",
                        OverviewView.Tags(applied.Wishes, applied.Musts, applied.Forbidden)),
                    "caption"),
            ],
        };
    }

    /// <summary>A select that offers following the library first, then its own values.</summary>
    private static PluginFormField Select(
        string name,
        string label,
        string? value,
        string libraryValue,
        IReadOnlyList<string> offered)
    {
        return new()
        {
            Name = name,
            Label = label,
            Type = PluginFormFieldType.Select,
            Value = value ?? ShowSettingsEdit.Follow,
            Options = [Follows(libraryValue), .. offered.Select(one => new PluginFormOption { Value = one, Label = one })],
        };
    }

    /// <summary>The empty choice, which says what following the library comes to.</summary>
    private static PluginFormOption Follows(string libraryValue)
    {
        return new() { Value = ShowSettingsEdit.Follow, Label = $"Follow the library ({libraryValue})" };
    }

    /// <summary>For each tag list a comma-separated field and a field for one tag appended on Save.</summary>
    internal static IEnumerable<PluginFormField> TagFields(
        IReadOnlyList<string> wishes,
        IReadOnlyList<string> musts,
        IReadOnlyList<string> forbidden)
    {
        // An example in every box: six empty fields in a row say nothing about
        // what belongs in them, which is the owner's report of 16 September
        // 2026. Each is a tag off a real release name.
        foreach ((string name, string label, IReadOnlyList<string> tags, string example) in new[]
                 {
                     (ShowSettingsEdit.WishesField, "Wishes", wishes, "ATVP, Atmos"),
                     (ShowSettingsEdit.MustsField, "Musts", musts, "WEB-DL"),
                     (ShowSettingsEdit.ForbiddenField, "Forbidden", forbidden, "HDTV, XviD"),
                 })
        {
            yield return new PluginFormField
            {
                Name = name,
                Label = $"{label}, separated by commas",
                Value = string.Join(", ", tags),
                Placeholder = example,
            };

            yield return new PluginFormField
            {
                Name = name + ShowSettingsEdit.AddSuffix,
                Label = $"Add one to {label.ToLowerInvariant()}",
                Value = string.Empty,
                Placeholder = example.Split(',')[0],
            };
        }
    }
}

using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// The preferences form of one library, at <c>/libraries/:id</c>.
/// </summary>
/// <remarks>
/// <c>docs/specs/show-list.md</c> § The settings form: the same form as a show's, without the choice of
/// following the library, because this is the library. Its quality may be left unset, and then no show
/// of it that follows the library searches anything.
/// </remarks>
public static class LibraryPreferencesView
{
    public const string FormId = "library-preferences";

    public static PluginView Render(Library library, LibraryPreferences preferences)
    {
        return new()
        {
            Layout = PluginLayout.Wide,
            Components =
            [
                Ui.Text("library-title", library.Name, "title"),
                Ui.Text("library-kind", library.Kind == LibraryKind.Anime ? "anime library" : "tv library", "caption"),
                Ui.Form(
                    FormId,
                    "Save",
                    PluginActionIntent.CallPlugin($"libraries/{library.Id}/preferences", null, PluginActionTransport.Rest),
                    [
                        new PluginFormField
                        {
                            Name = ShowSettingsEdit.QualityField,
                            Label = "Quality",
                            Type = PluginFormFieldType.Select,
                            Value = preferences.Quality ?? string.Empty,
                            Options =
                            [
                                new() { Value = string.Empty, Label = "Not set" },
                                .. LibraryPreferences.Qualities.Select(one => new PluginFormOption { Value = one, Label = one }),
                            ],
                        },
                        new PluginFormField
                        {
                            Name = ShowSettingsEdit.CodecField,
                            Label = "Codec",
                            Type = PluginFormFieldType.Select,
                            Value = preferences.Codec,
                            Options = [.. LibraryPreferences.Codecs.Select(one => new PluginFormOption { Value = one, Label = one })],
                        },
                        new PluginFormField
                        {
                            Name = ShowSettingsEdit.SpecialsField,
                            Label = "Specials (season 0)",
                            Type = PluginFormFieldType.Toggle,
                            Value = preferences.Specials,
                        },
                        new PluginFormField
                        {
                            Name = ShowSettingsEdit.EnglishOnlyField,
                            Label = "English only",
                            Type = PluginFormFieldType.Toggle,
                            Value = preferences.EnglishOnly,
                        },
                        .. ShowSettingsView.TagFields(preferences.Wishes, preferences.Musts, preferences.Forbidden),
                    ]),
            ],
        };
    }
}

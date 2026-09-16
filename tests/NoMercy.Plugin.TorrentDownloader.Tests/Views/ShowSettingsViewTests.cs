using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>
/// The settings form of one show and of one library. <c>docs/specs/show-list.md</c> § The settings form.
/// </summary>
public class ShowSettingsViewTests
{
    private const string Tv = "01HQ5W4AVF30N10RT6XCF6AJHM";

    [Fact]
    public void OneFormOneSaveWithFollowTheLibraryForQualityCodecAndSpecials()
    {
        LibraryPreferences series = new(Tv) { Quality = "1080p" };

        PluginView page = ShowSettingsView.Render(Silo(), new(41) { SwitchedOn = true, Saved = true }, series);

        PluginComponent form = Assert.Single(Rendered.All(page), one => one.Component == Ui.FormComponent);

        Assert.Equal("shows/41/settings", form.Action!.Payload["method"]);

        IReadOnlyList<PluginFormField> fields = Fields(form);

        Assert.Equal(
            ["switchedOn", "quality", "codec", "specials", "englishOnly", "wishes", "wishes.add", "musts", "musts.add", "forbidden", "forbidden.add"],
            fields.Select(field => field.Name));

        foreach (string followed in new[] { "quality", "codec", "specials" })
        {
            PluginFormField choice = fields.Single(field => field.Name == followed);

            Assert.Equal(PluginFormFieldType.Select, choice.Type);
            Assert.Equal(string.Empty, choice.Options[0].Value);
            Assert.Contains("library", choice.Options[0].Label, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(LibraryPreferences.Qualities, fields.Single(field => field.Name == "quality").Options.Skip(1).Select(option => option.Value));
        Assert.Equal(LibraryPreferences.Codecs, fields.Single(field => field.Name == "codec").Options.Skip(1).Select(option => option.Value));
    }

    [Fact]
    public void TheFormHoldsWhatWasSaved()
    {
        ShowSettings silo = new(41)
        {
            SwitchedOn = true,
            Saved = true,
            Quality = "2160p",
            Specials = false,
            Wishes = ["WEB", "NTb"],
            Forbidden = ["HDR"],
        };

        IReadOnlyList<PluginFormField> fields = Fields(Assert.Single(
            Rendered.All(ShowSettingsView.Render(Silo(), silo, new(Tv))),
            one => one.Component == Ui.FormComponent));

        Assert.Equal(true, Value(fields, "switchedOn"));
        Assert.Equal("2160p", Value(fields, "quality"));
        Assert.Equal(string.Empty, Value(fields, "codec"));
        Assert.Equal("off", Value(fields, "specials"));
        Assert.Equal("WEB, NTb", Value(fields, "wishes"));
        Assert.Equal("HDR", Value(fields, "forbidden"));
        Assert.Equal(string.Empty, Value(fields, "wishes.add"));
    }

    [Fact]
    public void BelowTheFormIsWhatAppliesWithTheLibraryCountedIn()
    {
        LibraryPreferences series = new(Tv) { Quality = "720p", Codec = "h265", Forbidden = ["DUAL"] };

        PluginView page = ShowSettingsView.Render(Silo(), new(41) { SwitchedOn = true, Saved = true, Wishes = ["NTb"] }, series);

        PluginComponent applied = Rendered.ById(page, ShowSettingsView.AppliedId);
        string words = string.Join(" ", Rendered.EveryValue(new PluginView { Components = [applied] }));

        Assert.Contains("720p", words, StringComparison.Ordinal);
        Assert.Contains("h265", words, StringComparison.Ordinal);
        Assert.Contains("DUAL", words, StringComparison.Ordinal);
        Assert.Contains("NTb", words, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameFormWithoutFollowTheLibrary()
    {
        PluginView page = LibraryPreferencesView.Render(
            new(Tv, "Series", LibraryKind.Television),
            new(Tv) { Quality = "1080p", Specials = true });

        PluginComponent form = Assert.Single(Rendered.All(page), one => one.Component == Ui.FormComponent);

        Assert.Equal($"libraries/{Tv}/preferences", form.Action!.Payload["method"]);

        IReadOnlyList<PluginFormField> fields = Fields(form);

        Assert.Equal(
            ["quality", "codec", "specials", "englishOnly", "wishes", "wishes.add", "musts", "musts.add", "forbidden", "forbidden.add"],
            fields.Select(field => field.Name));
        Assert.DoesNotContain(
            fields.SelectMany(field => field.Options),
            option => option.Label.Contains("library", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PluginFormFieldType.Toggle, fields.Single(field => field.Name == "specials").Type);
        Assert.Equal(LibraryPreferences.Codecs, fields.Single(field => field.Name == "codec").Options.Select(option => option.Value));
    }

    private static Show Silo()
    {
        return new(41, "Silo", 2023, Tv, "Series", LibraryKind.Television, "/Silo.(2023)");
    }

    private static IReadOnlyList<PluginFormField> Fields(PluginComponent form)
    {
        return Assert.IsAssignableFrom<IReadOnlyList<PluginFormField>>(form.Props["fields"]);
    }

    private static object? Value(IReadOnlyList<PluginFormField> fields, string name)
    {
        return fields.Single(field => field.Name == name).Value;
    }
}

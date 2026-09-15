using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Configuration;

/// <summary>
/// What the settings form of a show or a library posts, put into its settings.
/// <c>docs/specs/show-list.md</c> § The settings form.
/// </summary>
public class ShowSettingsEditTests
{
    [Fact]
    public void ATagTypedInTheOneTagFieldIsAppendedToTheEndOfItsList()
    {
        (ShowSettings saved, IReadOnlyList<string> refused) = ShowSettingsEdit.Show(
            new(41) { Wishes = ["WEB"] },
            new Dictionary<string, string?>
            {
                ["wishes"] = "WEB, AMZN",
                ["wishes.add"] = "NTb",
                ["forbidden"] = string.Empty,
                ["forbidden.add"] = " HDR ",
            });

        Assert.Empty(refused);
        Assert.Equal(["WEB", "AMZN", "NTb"], saved.Wishes);
        Assert.Equal(["HDR"], saved.Forbidden);
    }

    [Fact]
    public void ACommaListIsSplitIntoTags()
    {
        (ShowSettings saved, _) = ShowSettingsEdit.Show(
            new(41),
            new Dictionary<string, string?> { ["musts"] = "WEB, NTb ,, AMZN " });

        Assert.Equal(["WEB", "NTb", "AMZN"], saved.Musts);
    }

    [Fact]
    public void ATagAlreadyInTheListIsNotAddedTwice()
    {
        (ShowSettings saved, _) = ShowSettingsEdit.Show(
            new(41),
            new Dictionary<string, string?> { ["wishes"] = "WEB", ["wishes.add"] = "web" });

        Assert.Equal(["WEB"], saved.Wishes);
    }

    [Fact]
    public void TheEmptyChoiceFollowsTheLibrary()
    {
        (ShowSettings saved, IReadOnlyList<string> refused) = ShowSettingsEdit.Show(
            new(41) { Quality = "720p", Codec = "h265", Specials = true },
            new Dictionary<string, string?>
            {
                ["switchedOn"] = "true",
                ["quality"] = string.Empty,
                ["codec"] = string.Empty,
                ["specials"] = string.Empty,
            });

        Assert.Empty(refused);
        Assert.True(saved.SwitchedOn);
        Assert.Null(saved.Quality);
        Assert.Null(saved.Codec);
        Assert.Null(saved.Specials);
    }

    [Fact]
    public void AShowsOwnChoicesAreKept()
    {
        (ShowSettings saved, _) = ShowSettingsEdit.Show(
            new(41),
            new Dictionary<string, string?>
            {
                ["switchedOn"] = "false",
                ["quality"] = "2160p",
                ["codec"] = "h264",
                ["specials"] = "on",
            });

        Assert.False(saved.SwitchedOn);
        Assert.Equal("2160p", saved.Quality);
        Assert.Equal("h264", saved.Codec);
        Assert.True(saved.Specials);
    }

    /// <remarks>
    /// A quality or codec the page never offers would refuse every release name there is, silently.
    /// Refused with the field named, and nothing of the post is taken.
    /// </remarks>
    [Fact]
    public void AQualityOrCodecThePageDoesNotOfferIsRefusedAndNothingIsTaken()
    {
        ShowSettings before = new(41) { Quality = "720p" };

        (ShowSettings saved, IReadOnlyList<string> refused) = ShowSettingsEdit.Show(
            before,
            new Dictionary<string, string?> { ["quality"] = "1080", ["codec"] = "hevc", ["wishes"] = "WEB" });

        Assert.Equal(2, refused.Count);
        Assert.Contains(refused, reason => reason.Contains("quality", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(refused, reason => reason.Contains("codec", StringComparison.OrdinalIgnoreCase));
        Assert.Same(before, saved);
    }

    [Fact]
    public void ALibrarysPreferencesAreTakenWithoutAFollowChoice()
    {
        (LibraryPreferences saved, IReadOnlyList<string> refused) = ShowSettingsEdit.Library(
            new LibraryPreferences("01HQ5W4AVF30N10RT6XCF6AJHM"),
            new Dictionary<string, string?>
            {
                ["quality"] = "1080p",
                ["codec"] = "h265",
                ["specials"] = "true",
                ["forbidden"] = "DUAL",
                ["forbidden.add"] = "VOSTFR",
            });

        Assert.Empty(refused);
        Assert.Equal("1080p", saved.Quality);
        Assert.Equal("h265", saved.Codec);
        Assert.True(saved.Specials);
        Assert.Equal(["DUAL", "VOSTFR"], saved.Forbidden);

        (_, IReadOnlyList<string> noCodec) = ShowSettingsEdit.Library(
            new LibraryPreferences("01HQ5W4AVF30N10RT6XCF6AJHM"),
            new Dictionary<string, string?> { ["codec"] = string.Empty });

        Assert.Single(noCodec);
    }
}

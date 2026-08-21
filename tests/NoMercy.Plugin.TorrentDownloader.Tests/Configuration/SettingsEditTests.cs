using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Configuration;

/// <summary>
/// One edited field, applied to the settings that already exist.
/// </summary>
/// <remarks>
/// <para>
/// A form posts what its fields hold and nothing else — flat names, string
/// values, no structure. The settings are nested, so something has to put one
/// into the other, and that is this.
/// </para>
/// <para>
/// It applies and refuses; it does not validate. <see cref="SettingsStore"/>
/// does that, once, for both ways in.
/// </para>
/// </remarks>
public class SettingsEditTests
{
    [Fact]
    public void AFieldIsAppliedWhereItBelongs()
    {
        Settings settings = new();

        IReadOnlyList<string> problems = SettingsEdit.Apply(
            settings,
            new Dictionary<string, string?> { ["client.listenPort"] = "6881" });

        Assert.Empty(problems);
        Assert.Equal(6881, settings.Client.ListenPort);
    }

    /// <remarks>
    /// A form posts every field it holds on every save, so all but one of them
    /// is arriving unchanged. Applying only what is named is what stops a page
    /// that does not carry a setting from quietly clearing it.
    /// </remarks>
    [Fact]
    public void WhatIsNotNamedIsLeftAsItWas()
    {
        Settings settings = new();
        settings.Client.ListenPort = 6881;

        SettingsEdit.Apply(settings, new Dictionary<string, string?> { ["dryRun"] = "true" });

        Assert.Equal(6881, settings.Client.ListenPort);
        Assert.True(settings.DryRun);
    }

    /// <remarks>
    /// Refused by name rather than ignored. A field this does not know is a
    /// field the owner filled in and watched save, and a silent skip leaves
    /// them believing a setting they can see took effect.
    /// </remarks>
    [Fact]
    public void AFieldNothingAnswersToIsRefusedByName()
    {
        IReadOnlyList<string> problems = SettingsEdit.Apply(
            new(),
            new Dictionary<string, string?> { ["client.listenPortt"] = "6881" });

        Assert.Contains(problems, problem => problem.Contains("client.listenPortt", StringComparison.Ordinal));
    }

    /// <remarks>
    /// The value is refused with the field named, and nothing is applied. A
    /// number field can still arrive holding words: the browser is not the only
    /// thing that posts here.
    /// </remarks>
    [Fact]
    public void AValueOfTheWrongShapeIsRefusedAndChangesNothing()
    {
        Settings settings = new();

        IReadOnlyList<string> problems = SettingsEdit.Apply(
            settings,
            new Dictionary<string, string?> { ["client.listenPort"] = "not a port" });

        Assert.Contains(problems, problem => problem.Contains("client.listenPort", StringComparison.Ordinal));
        Assert.Equal(51413, settings.Client.ListenPort);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("on", true)]
    public void AToggleArrivesAsWhateverTheClientCallsIt(string value, bool expected)
    {
        Settings settings = new();
        settings.Profile.EnglishOnly = !expected;

        IReadOnlyList<string> problems = SettingsEdit.Apply(
            settings,
            new Dictionary<string, string?> { ["profile.englishOnly"] = value });

        Assert.Empty(problems);
        Assert.Equal(expected, settings.Profile.EnglishOnly);
    }

    /// <remarks>
    /// Terms are typed as one line because that is how a text field takes a
    /// list. Splitting on the comma is what makes the field usable at all.
    /// </remarks>
    [Fact]
    public void AListIsTypedAsOneLineAndSplitOnTheComma()
    {
        Settings settings = new();

        SettingsEdit.Apply(
            settings,
            new Dictionary<string, string?> { ["profile.excludeTerms"] = "HDCAM, CAM ,,TS" });

        Assert.Equal(["HDCAM", "CAM", "TS"], settings.Profile.ExcludeTerms);
    }

    /// <remarks>
    /// Every field the page offers has somewhere to land. A page that renders a
    /// field this cannot apply is a control the owner can type into and never
    /// change anything with.
    /// </remarks>
    [Fact]
    public void EveryFieldTheSettingsPageOffersCanBeApplied()
    {
        foreach (string name in SettingsEdit.Fields)
        {
            IReadOnlyList<string> problems = SettingsEdit.Apply(
                new(),
                new Dictionary<string, string?> { [name] = Sample(name) });

            Assert.Empty(problems);
        }
    }

    /// <summary>A value of the right shape for whatever this field holds.</summary>
    private static string Sample(string name)
    {
        return name switch
        {
            "cadences.transfers" or "cadences.feed" or "cadences.search" or "cadences.maintenance"
                => "0 4 * * *",
            "profile.maximumResolution" => "1080p",
            "profile.codec" => Profile.AnyCodec,
            "client.encryption" => nameof(EncryptionPolicy.Allowed),
            _ => Shape(name),
        };
    }

    private static string Shape(string name)
    {
        Settings settings = new();

        // Whatever the field already holds is by definition the right shape for
        // it, so the sample comes from the settings rather than from a table
        // here that would drift away from them.
        return SettingsEdit.Read(settings, name) ?? "1";
    }
}

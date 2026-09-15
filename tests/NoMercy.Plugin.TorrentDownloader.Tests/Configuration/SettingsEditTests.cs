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

        SettingsEdit.Apply(settings, new Dictionary<string, string?> { ["client.stallMinutes"] = "45" });

        Assert.Equal(6881, settings.Client.ListenPort);
        Assert.Equal(45, settings.Client.StallMinutes);
    }

    /// <remarks>
    /// <c>docs/specs/show-list.md</c>: quality, codec, tags and language are set per show and per library
    /// on the overview, and the settings page no longer carries them. A post that still names one is
    /// refused by name, like any field nothing answers to, rather than written into a profile no page
    /// shows.
    /// </remarks>
    [Theory]
    [InlineData("profile.maximumResolution")]
    [InlineData("profile.codec")]
    [InlineData("profile.requireCodecTag")]
    [InlineData("profile.englishOnly")]
    [InlineData("profile.includeSpecials")]
    [InlineData("profile.excludeTerms")]
    public void AQualityCodecTagOrLanguageFieldIsNoLongerASetting(string name)
    {
        IReadOnlyList<string> problems = SettingsEdit.Apply(
            new(),
            new Dictionary<string, string?> { [name] = "1080p" });

        Assert.Contains(problems, problem => problem.Contains(name, StringComparison.Ordinal));
        Assert.DoesNotContain(name, SettingsEdit.Fields);
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
        Assert.Equal(6881, settings.Client.ListenPort);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("on", true)]
    public void AToggleArrivesAsWhateverTheClientCallsIt(string value, bool expected)
    {
        Settings settings = new();

        if (expected)
        {
            settings.DisabledDefaultSources.Add("eztv");
        }

        IReadOnlyList<string> problems = SettingsEdit.Apply(
            settings,
            new Dictionary<string, string?> { [SettingsEdit.SourcePrefix + "eztv"] = value });

        Assert.Empty(problems);
        Assert.Equal(expected, !settings.DisabledDefaultSources.Contains("eztv"));
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
            "cadences.cycle" => "0 4 * * *",
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

    /// <remarks>
    /// <para>
    /// <strong>The page stops asking the owner to type 10485760.</strong> The
    /// limits are stored in bytes per second and everything downstream reads
    /// them that way, so nothing there changes — but a speed is a thing people
    /// think about in megabytes, and the presets are the common answers.
    /// </para>
    /// <para>
    /// The preset carries bytes because that is what it stands for. The box
    /// beside it is megabytes per second, because that is what somebody typing
    /// a number into a box labelled MB/s means, and it wins when it is filled
    /// in: a preset list can only ever hold the answers somebody thought of.
    /// Nought is unlimited, which the preset says and the box cannot.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASpeedIsTypedInMegabytesAndStoredInBytes()
    {
        Settings settings = new();

        // A preset, in the bytes it stands for.
        Assert.Empty(SettingsEdit.Apply(
            settings,
            new Dictionary<string, string?> { ["client.maxDownloadRate"] = "10485760" }));

        Assert.Equal(10485760, settings.Client.MaxDownloadRate);

        // The box, in megabytes a second.
        Assert.Empty(SettingsEdit.Apply(
            settings,
            new Dictionary<string, string?> { ["client.maxUploadRateMb"] = "3" }));

        Assert.Equal(3 * 1024 * 1024, settings.Client.MaxUploadRate);

        // Both, which is what a form that draws both posts. What was typed
        // wins, or the box would be a control that silently did nothing.
        Assert.Empty(SettingsEdit.Apply(
            settings,
            new Dictionary<string, string?>
            {
                ["client.maxDownloadRate"] = "1048576",
                ["client.maxDownloadRateMb"] = "25",
            }));

        Assert.Equal(25L * 1024 * 1024, settings.Client.MaxDownloadRate);

        // And nought is unlimited, which only the preset can say.
        Assert.Empty(SettingsEdit.Apply(
            settings,
            new Dictionary<string, string?>
            {
                ["client.maxDownloadRate"] = "0",
                ["client.maxDownloadRateMb"] = "",
            }));

        Assert.Equal(0, settings.Client.MaxDownloadRate);
    }

    /// <remarks>
    /// Every field behind <strong>Show advanced</strong>, typed and read back.
    /// The switch is a display state: a field hidden behind it still applies,
    /// so each one has to survive a save exactly as any other does. One that
    /// only worked while the block was open would be a setting that depended on
    /// whether the owner had clicked something.
    /// </remarks>
    [Fact]
    public void EveryAdvancedFieldRoundTripsThroughSave()
    {
        Settings settings = new();

        Dictionary<string, string?> typed = new()
        {
            ["client.stallMinutes"] = "45",
            ["client.metadataTimeoutMinutes"] = "7",
            ["client.encryption"] = nameof(EncryptionPolicy.Required),
            ["client.resumeIntervalSeconds"] = "120",
            ["cadences.cycle"] = "0 */3 * * *",
        };

        Assert.Empty(SettingsEdit.Apply(settings, typed));

        Assert.Equal(45, settings.Client.StallMinutes);
        Assert.Equal(7, settings.Client.MetadataTimeoutMinutes);
        Assert.Equal(EncryptionPolicy.Required, settings.Client.Encryption);
        Assert.Equal(120, settings.Client.ResumeIntervalSeconds);
        Assert.Equal("0 */3 * * *", settings.Cadences.Cycle);
    }
}

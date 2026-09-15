using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Domain;

/// <summary>
/// What applies to one show, with its library's preferences counted in.
/// </summary>
/// <remarks>
/// The owner's requirements of 15 September 2026, <c>docs/specs/show-list.md</c> § Library
/// preferences: a show follows its library for quality, codec and specials until it sets its own, and
/// its tag lists are the library's and its own together.
/// </remarks>
public class EffectiveSettingsTests
{
    [Fact]
    public void AShowFollowsItsLibraryUntilItSetsItsOwn()
    {
        LibraryPreferences library = new("tv") { Quality = "1080p", Codec = "h264", Specials = true };

        EffectiveSettings following = EffectiveSettings.Of(Saved(), library);

        Assert.Equal("1080p", following.Quality);
        Assert.Equal("h264", following.Codec);
        Assert.True(following.Specials);

        EffectiveSettings own = EffectiveSettings.Of(
            Saved() with { Quality = "2160p", Codec = "h265", Specials = false },
            library);

        Assert.Equal("2160p", own.Quality);
        Assert.Equal("h265", own.Codec);
        Assert.False(own.Specials);
    }

    [Fact]
    public void TheTagListsAreTheLibrarysAndTheShowsTogether()
    {
        LibraryPreferences library = new("tv")
        {
            Quality = "1080p",
            Wishes = ["WEB"],
            Musts = ["H264"],
            Forbidden = ["HDR"],
        };

        EffectiveSettings applied = EffectiveSettings.Of(
            Saved() with { Wishes = ["NTb"], Musts = ["AMZN"], Forbidden = ["MULTi"] },
            library);

        Assert.Equal(["WEB", "NTb"], applied.Wishes);
        Assert.Equal(["H264", "AMZN"], applied.Musts);
        Assert.Equal(["HDR", "MULTi"], applied.Forbidden);
    }

    /// <remarks>
    /// The library forbids <c>DUAL</c> for every show; this show wants it. The show's word is the one
    /// that counts, whatever the case the two were typed in.
    /// </remarks>
    [Fact]
    public void ATagTheLibraryAndTheShowPutInDifferentListsCountsOnlyInTheShowsList()
    {
        LibraryPreferences library = new("anime") { Quality = "1080p", Forbidden = ["DUAL"], Wishes = ["VOSTFR"] };

        EffectiveSettings applied = EffectiveSettings.Of(
            Saved() with { Wishes = ["dual"], Forbidden = ["vostfr"] },
            library);

        Assert.Equal(["dual"], applied.Wishes);
        Assert.Equal(["vostfr"], applied.Forbidden);
    }

    [Fact]
    public void AShowWithNoQualityAnywhereSearchesNothing()
    {
        EffectiveSettings applied = EffectiveSettings.Of(Saved(), new LibraryPreferences("tv"));

        Assert.Null(applied.Quality);
        Assert.False(applied.Searched);
    }

    [Fact]
    public void AShowSwitchedOnButNeverSavedSearchesNothing()
    {
        LibraryPreferences library = new("tv") { Quality = "1080p" };

        Assert.False(EffectiveSettings.Of(Saved() with { Saved = false }, library).Searched);
        Assert.False(EffectiveSettings.Of(Saved() with { SwitchedOn = false }, library).Searched);
        Assert.True(EffectiveSettings.Of(Saved(), library).Searched);
    }

    /// <remarks>
    /// <c>docs/specs/show-list.md</c>: a library's codec is any until the owner chooses another, and its
    /// specials are off until the owner switches them on.
    /// </remarks>
    [Fact]
    public void ALibraryNobodyTouchedHasAnyCodecAndSpecialsOff()
    {
        LibraryPreferences untouched = new("tv");

        Assert.Equal(LibraryPreferences.AnyCodec, untouched.Codec);
        Assert.False(untouched.Specials);
        Assert.Null(untouched.Quality);
        Assert.Empty(untouched.Wishes);
        Assert.Empty(untouched.Musts);
        Assert.Empty(untouched.Forbidden);
    }

    private static ShowSettings Saved()
    {
        return new(41) { SwitchedOn = true, Saved = true };
    }
}

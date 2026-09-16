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

    /// <remarks>
    /// <strong>The owner's rule of 16 September 2026.</strong> A show switched on is searched with its
    /// library's settings straight away; only what the owner changes on the show itself overrules them.
    /// Seven shows switched on from their rows searched nothing for hours, because each was waiting for a
    /// Save of a form nobody had any reason to open.
    /// </remarks>
    [Fact]
    public void AShowSwitchedOnIsSearchedWithItsLibrarysSettingsWithoutBeingSaved()
    {
        LibraryPreferences library = new("tv") { Quality = "1080p" };

        EffectiveSettings applied = EffectiveSettings.Of(new ShowSettings(41) { SwitchedOn = true }, library);

        Assert.True(applied.Searched);
        Assert.Equal("1080p", applied.Quality);
        Assert.False(EffectiveSettings.Of(new ShowSettings(41) { SwitchedOn = false }, library).Searched);
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
        return new(41) { SwitchedOn = true };
    }
}

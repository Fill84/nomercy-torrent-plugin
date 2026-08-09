// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Profiles;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Profiles;

public class QualityLaddersTests
{
    [Fact]
    public void UpTo_StopsAtTheResolutionItWasGiven()
    {
        QualityLadder ladder = QualityLadders.UpTo(Resolution.Fhd1080);

        ladder.Ordered.Select(rung => rung.Resolution).Should().Equal(Resolution.Sd480, Resolution.Sd576, Resolution.Hd720, Resolution.Fhd1080);
    }

    // The cutoff is what "good enough, stop looking" means. Anywhere but the top rung and
    // the plugin either keeps hunting for something it will not accept, or settles below
    // what the owner asked for.
    [Fact]
    public void UpTo_PutsTheCutoffOnTheTopRung()
    {
        QualityLadder ladder = QualityLadders.UpTo(Resolution.Fhd1080);

        ladder.CutoffRank.Should().Be(ladder.Ordered.Count - 1);
    }

    [Fact]
    public void UpTo_RanksABetterReleaseHigher()
    {
        QualityLadder ladder = QualityLadders.UpTo(Resolution.Uhd2160);

        int hd = ladder.RankOf(new Quality(Resolution.Hd720, ReleaseSource.WebDl));
        int uhd = ladder.RankOf(new Quality(Resolution.Uhd2160, ReleaseSource.WebDl));

        uhd.Should().BeGreaterThan(hd);
    }

    // Asked for 720p means 1080p is off the ladder entirely, not merely ranked lower: a
    // rung that is not there cannot be chosen, which is what "maximum" has to mean or the
    // setting is a preference the scorer can talk itself out of.
    [Fact]
    public void UpTo_LeavesAnythingAboveTheMaximumOffTheLadder()
    {
        QualityLadder ladder = QualityLadders.UpTo(Resolution.Hd720);

        ladder.RankOf(new Quality(Resolution.Fhd1080, ReleaseSource.WebDl)).Should().BeLessThan(0);
    }

    [Fact]
    public void UpTo_RefusesAResolutionThatIsNotOne()
    {
        Action build = () => QualityLadders.UpTo(Resolution.Unknown);

        build.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("720p", Resolution.Hd720)]
    [InlineData("1080p", Resolution.Fhd1080)]
    [InlineData("2160p", Resolution.Uhd2160)]
    [InlineData("4k", Resolution.Uhd2160)]
    [InlineData("  1080P  ", Resolution.Fhd1080)]
    public void Parse_ReadsWhatAPersonWouldType(string text, Resolution expected)
    {
        QualityLadders.ParseResolution(text, Resolution.Sd480).Should().Be(expected);
    }

    // A stored setting from a future version, or a hand-edited config, must not stop the
    // plugin from running - it falls back to what the caller says is sensible.
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("enormous")]
    public void Parse_FallsBackWhenItCannotTellWhatWasMeant(string? text)
    {
        QualityLadders.ParseResolution(text, Resolution.Fhd1080).Should().Be(Resolution.Fhd1080);
    }
}

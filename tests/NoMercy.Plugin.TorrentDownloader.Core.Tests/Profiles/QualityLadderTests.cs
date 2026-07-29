using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Profiles;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Profiles;

public class QualityLadderTests
{
    private static QualityLadder Ladder() =>
        new(
            [
                new QualityDefinition("HDTV-720p", Resolution.Hd720, ReleaseSource.Hdtv),
                new QualityDefinition("WEB-720p", Resolution.Hd720, ReleaseSource.Unknown),
                new QualityDefinition("WEB-1080p", Resolution.Fhd1080, ReleaseSource.Unknown),
                new QualityDefinition("BluRay-1080p", Resolution.Fhd1080, ReleaseSource.BluRay),
            ],
            "WEB-1080p"
        );

    [Fact]
    public void RankOf_OrdersByLadderPosition()
    {
        QualityLadder ladder = Ladder();

        ladder.RankOf(new Quality(Resolution.Fhd1080, ReleaseSource.BluRay)).Should().Be(3);
        ladder.RankOf(new Quality(Resolution.Fhd1080, ReleaseSource.WebDl)).Should().Be(2);
        ladder.RankOf(new Quality(Resolution.Hd720, ReleaseSource.Hdtv)).Should().Be(0);
    }

    [Fact]
    public void RankOf_ReturnsMinusOneForAQualityNotOnTheLadder()
    {
        Ladder().RankOf(new Quality(Resolution.Uhd2160, ReleaseSource.WebDl)).Should().Be(-1);
    }

    [Fact]
    public void RankOf_PrefersTheMostSpecificRung()
    {
        Ladder()
            .RankOf(new Quality(Resolution.Hd720, ReleaseSource.Hdtv))
            .Should()
            .Be(0, "the HDTV-specific rung must win over the source-agnostic WEB-720p rung");
    }

    [Fact]
    public void IsAllowed_IsTrueOnlyForQualitiesOnTheLadder()
    {
        QualityLadder ladder = Ladder();

        ladder.IsAllowed(new Quality(Resolution.Fhd1080, ReleaseSource.WebDl)).Should().BeTrue();
        ladder.IsAllowed(new Quality(Resolution.Sd480, ReleaseSource.WebRip)).Should().BeFalse();
    }

    [Fact]
    public void MeetsCutoff_IsTrueAtOrAboveTheCutoffRung()
    {
        QualityLadder ladder = Ladder();

        ladder.MeetsCutoff(new Quality(Resolution.Fhd1080, ReleaseSource.WebDl)).Should().BeTrue();
        ladder.MeetsCutoff(new Quality(Resolution.Fhd1080, ReleaseSource.BluRay)).Should().BeTrue();
        ladder.MeetsCutoff(new Quality(Resolution.Hd720, ReleaseSource.Hdtv)).Should().BeFalse();
    }

    [Fact]
    public void MeetsCutoff_IsFalseForAQualityNotOnTheLadder()
    {
        Ladder().MeetsCutoff(new Quality(Resolution.Uhd2160, ReleaseSource.WebDl)).Should().BeFalse();
    }
}

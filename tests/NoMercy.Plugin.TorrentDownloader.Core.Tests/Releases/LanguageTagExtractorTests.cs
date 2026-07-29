using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Releases;

public class LanguageTagExtractorTests
{
    [Theory]
    [InlineData("Silo.S03E04.FRENCH.1080p.WEB.H264", "French")]
    [InlineData("Silo.S03E04.GERMAN.1080p.WEB.H264", "German")]
    [InlineData("Silo.S03E04.ITA.1080p.WEB.H264", "Italian")]
    [InlineData("Silo.S03E04.SPANISH.1080p", "Spanish")]
    [InlineData("Silo.S03E04.POLISH.1080p", "Polish")]
    [InlineData("Silo.S03E04.JPN.1080p", "Japanese")]
    [InlineData("Silo.S03E04.NL.1080p", "Dutch")]
    public void Extract_RecognisesLanguageTags(string title, string expected)
    {
        LanguageTagExtractor.Extract(title).Languages.Should().Contain(expected);
    }

    [Fact]
    public void Extract_ReportsBothLanguagesOfAMultiAudioRelease()
    {
        LanguageTags tags = LanguageTagExtractor.Extract("Silo.S03E04.ITA.ENG.1080p.WEB.H264");

        tags.Languages.Should().BeEquivalentTo(["Italian", "English"]);
    }

    [Fact]
    public void Extract_TreatsAnUntaggedReleaseAsEnglish()
    {
        LanguageTags tags = LanguageTagExtractor.Extract("Silo.S03E04.1080p.WEB.H264-CAKES");

        tags.Languages.Should().BeEquivalentTo(["English"]);
        tags.IsDualAudio.Should().BeFalse();
    }

    [Theory]
    [InlineData("Frieren S01E01 1080p Dual Audio")]
    [InlineData("Frieren S01E01 1080p Dual-Audio")]
    [InlineData("Frieren S01E01 1080p DUAL")]
    [InlineData("Silo S03E04 MULTi 1080p")]
    public void Extract_DetectsDualAudioMarkers(string title)
    {
        LanguageTagExtractor.Extract(title).IsDualAudio.Should().BeTrue();
    }

    [Theory]
    [InlineData("Serie.S01E01.[Cap.101].1080p", "Spanish")]
    [InlineData("Serie.S01E01.Capitulo.5.1080p", "Spanish")]
    [InlineData("Serie.Staffel.2.1080p", "German")]
    [InlineData("Serie.Odcinek.5.1080p", "Polish")]
    [InlineData("Serie.Saison.2.1080p", "French")]
    [InlineData("Serie.Seizoen.2.1080p", "Dutch")]
    public void Extract_RecognisesForeignEpisodeWordsWhenNoLanguageTagIsPresent(
        string title,
        string expected
    )
    {
        LanguageTagExtractor.Extract(title).Languages.Should().Contain(expected);
    }

    [Fact]
    public void Extract_DoesNotReadCaptainAsASpanishChapterMarker()
    {
        LanguageTagExtractor
            .Extract("Captain Show S01E01 1080p WEB H264-CAKES")
            .Languages
            .Should()
            .BeEquivalentTo(["English"]);
    }

    [Theory]
    [InlineData("Greek.S01E01.1080p.WEB.H264-GROUP")]
    [InlineData("Russian.Doll.S01E01.1080p.NF.WEB-DL-NTG")]
    [InlineData("Premier.League.PL.Matchday10.S01E01.1080p")]
    public void Extract_IgnoresLanguageWordsInTheShowName(string title)
    {
        LanguageTagExtractor.Extract(title).Languages.Should().BeEquivalentTo(["English"]);
    }

    [Theory]
    [InlineData("Russian.Doll.S02.1080p.NF.WEB-DL-NTb")]
    [InlineData("Greek.S01.1080p.WEB.H264-GROUP")]
    public void Extract_IgnoresLanguageWordsInTheShowNameOfASeasonPack(string title)
    {
        LanguageTagExtractor.Extract(title).Languages.Should().BeEquivalentTo(["English"]);
    }

    [Fact]
    public void Parse_FillsLanguageFieldsOnParsedRelease()
    {
        ParsedRelease parsed = ReleaseNameParser.Parse("Frieren S01E01 1080p WEB Dual Audio-Group");

        parsed.IsDualAudio.Should().BeTrue();
        parsed.Languages.Should().NotBeEmpty();
    }
}

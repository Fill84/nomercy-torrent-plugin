using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// Reading an episode off the files a library really holds.
/// </summary>
/// <remarks>
/// Every path here is one the owner's server wrote. The misfiled one is the
/// case this exists for: the encoder was asked for episode 153823, wrote
/// <c>South.Park.S15E12.1%.NoMercy.m3u8</c>, and the server attached the row to
/// episode 153785 — season 0.
/// </remarks>
public class LandedTests
{
    [Fact]
    public void AFileTheServerFiledUnderTheWrongEpisodeStillNamesTheRightOne()
    {
        Assert.True(Landed.Wrote(
            new(2190, 15, 12),
            ["/South.Park.(1997)/South.Park.S15E12/South.Park.S15E12.1%.NoMercy.m3u8"]));
    }

    /// <remarks>
    /// And it is the numbers that decide, not the show or the folder: a file of
    /// another episode of the same show is not this episode arriving. Without
    /// this the caller would delete a download because some other episode had
    /// been encoded.
    /// </remarks>
    [Fact]
    public void AnotherEpisodeOfTheSameShowIsNotThisOne()
    {
        Assert.False(Landed.Wrote(
            new(2190, 15, 12),
            [
                "/South.Park.(1997)/South.Park.S15E11/South.Park.S15E11.Broadway.Bro.Down.NoMercy.m3u8",
                "/South.Park.(1997)/South.Park.S16E12/South.Park.S16E12.NoMercy.m3u8",
            ]));
    }

    /// <remarks>
    /// A season and an episode of the same numbers the other way round is a
    /// different episode. S12E15 is not S15E12, and a rule that read the two
    /// numbers without keeping them apart would call it one.
    /// </remarks>
    [Fact]
    public void TheSeasonAndTheEpisodeAreNotInterchangeable()
    {
        Assert.False(Landed.Wrote(
            new(2190, 15, 12),
            ["/South.Park.(1997)/South.Park.S12E15/South.Park.S12E15.NoMercy.m3u8"]));
    }

    /// <remarks>
    /// Leading noughts and none, upper case and lower: the server writes
    /// <c>S03E06</c> and a good many release names write <c>s3e6</c>.
    /// </remarks>
    [Theory]
    [InlineData("/Silo/Silo.S03E06/Silo.S03E06.NoMercy.m3u8")]
    [InlineData("/Silo/Silo.s3e6/Silo.s3e6.mkv")]
    public void TheNumbersAreReadHoweverTheyAreSpelled(string path)
    {
        Assert.True(Landed.Wrote(new(125988, 3, 6), [path]));
    }

    /// <remarks>
    /// Nothing at all is not an arrival. A show whose folder the server has not
    /// written into answers with no files, and the caller must go on waiting
    /// rather than delete the download.
    /// </remarks>
    [Fact]
    public void NoFilesIsNotAnArrival()
    {
        Assert.False(Landed.Wrote(new(2190, 15, 12), []));
    }

    /// <remarks>
    /// A long-running show numbers past a hundred, and an absolute-numbered file
    /// of three digits must still be read.
    /// </remarks>
    [Fact]
    public void AnEpisodeNumberedPastAHundredIsRead()
    {
        Assert.True(Landed.Wrote(
            new(37854, 1, 137),
            ["/One.Piece/One.Piece.S01E137/One.Piece.S01E137.NoMercy.m3u8"]));
    }
}

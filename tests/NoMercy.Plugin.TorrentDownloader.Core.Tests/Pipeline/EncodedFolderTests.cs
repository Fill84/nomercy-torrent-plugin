using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// Which folder of the library holds an episode's encode, read off the paths the library hands back.
/// </summary>
/// <remarks>
/// <para>
/// The server's <c>VideoEncodeJob</c> writes an episode into <c>{library folder}/{Show.(Year)}/{Show.SxxEyy}/</c>
/// — the master playlist named for the episode, and its video, audio, subtitle and thumbnail files beside it —
/// and registers the master playlist. <c>GetShowFilesAsync</c> answers each registered file as
/// <c>HostFolder + Folder + Filename</c>.
/// </para>
/// <para>
/// The paths here are built from the rows of the owner's <c>VideoFiles</c> table as read on 15 October 2026.
/// The rows the encoder registered since September carry an absolute <c>HostFolder</c> that already ends in
/// the show's and the episode's folders, so the path the library answers names them twice; the older rows
/// carry a <c>HostFolder</c> relative to the library folder, which names no place on any disk.
/// </para>
/// </remarks>
public class EncodedFolderTests
{
    private const string Root = "Y:/nomercy/media/TV.Shows/TV.Shows";

    private const string ShowFolder = "/South.Park.(1997)";

    private static readonly EpisodeKey S29E03 = new(2190, 29, 3);

    private static readonly EpisodeKey S15E12 = new(2190, 15, 12);

    /// <remarks>
    /// <strong>The folder the encoder wrote, once, and the file the library names in it.</strong> The path the
    /// library answers names the show's and the episode's folders twice; the folder is where they are named
    /// once. Sabotage: taking the file's own directory hands the recycle area a folder that does not exist.
    /// </remarks>
    [Fact]
    public void TheEpisodesOwnFolderIsFoundWhereTheLibraryNamesItTwice()
    {
        EncodedLookup lookup = Replacement.EncodedOf(
            S29E03,
            ShowFolder,
            [
                $"{Root}/South.Park.(1997)/South.Park.S29E02/South.Park.(1997)/South.Park.S29E02/South.Park.S29E02.Billionaire.Weenietown.NoMercy.m3u8",
                $"{Root}/South.Park.(1997)/South.Park.S29E03/South.Park.(1997)/South.Park.S29E03/South.Park.S29E03.Episode.3.NoMercy.m3u8",
            ]);

        Assert.Equal(
            new EncodedEpisode(
                $"{Root}/South.Park.(1997)/South.Park.S29E03",
                $"{Root}/South.Park.(1997)/South.Park.S29E03/South.Park.S29E03.Episode.3.NoMercy.m3u8"),
            lookup.Found);
    }

    /// <remarks>
    /// A row that names the folders once — what the library answers where <c>HostFolder</c> is the library
    /// folder itself — is the same folder, with nothing to take off. Sabotage: always taking two folders off
    /// lands on the library folder.
    /// </remarks>
    [Fact]
    public void APathThatNamesTheFoldersOnceIsTheSameFolder()
    {
        EncodedLookup lookup = Replacement.EncodedOf(
            S29E03,
            ShowFolder,
            [$"{Root}/South.Park.(1997)/South.Park.S29E03/South.Park.S29E03.Episode.3.NoMercy.m3u8"]);

        Assert.Equal($"{Root}/South.Park.(1997)/South.Park.S29E03", lookup.Found?.Folder);
    }

    /// <remarks>
    /// <strong>A path relative to nothing is never one.</strong> S15E12 has three rows: two relative to a
    /// library folder — <c>South.Park.(1997)/South.Park.S15E12</c> and
    /// <c>TV.Shows/South.Park.(1997)/South.Park.S15E12</c> — and the encoder's absolute one. A relative path
    /// names no place on any disk, so the absolute one is the folder. Sabotage: accepting a relative path finds
    /// two folders and refuses both.
    /// </remarks>
    [Fact]
    public void ARelativePathIsNeverTheFolder()
    {
        EncodedLookup lookup = Replacement.EncodedOf(
            S15E12,
            ShowFolder,
            [
                "South.Park.(1997)/South.Park.S15E12/South.Park.(1997)/South.Park.S15E12/South.Park.S15E12.1%.NoMercy.m3u8",
                "TV.Shows/South.Park.(1997)/South.Park.S15E12/South.Park.(1997)/South.Park.S15E12/South.Park.S15E12.1%.NoMercy.m3u8",
                $"{Root}/South.Park.(1997)/South.Park.S15E12/South.Park.(1997)/South.Park.S15E12/South.Park.S15E12.1%.NoMercy.m3u8",
            ]);

        Assert.Equal($"{Root}/South.Park.(1997)/South.Park.S15E12", lookup.Found?.Folder);

        Assert.Null(Replacement.EncodedOf(
            S15E12,
            ShowFolder,
            ["TV.Shows/South.Park.(1997)/South.Park.S15E12/South.Park.(1997)/South.Park.S15E12/South.Park.S15E12.1%.NoMercy.m3u8"]).Found);
    }

    /// <remarks>
    /// <para>
    /// <strong>Never the show's folder, never another episode's, never somewhere else.</strong> A file sitting
    /// in the show's folder itself, a folder outside the show's, and a file named for this episode in another
    /// episode's folder are none of them this episode's encode — and each is refused with a reason rather than
    /// guessed at.
    /// </para>
    /// <para>
    /// Sabotage: dropping the check that the folder sits in the show's own folder takes the library folder's
    /// <c>South.Park.S29E03</c>; dropping the check that it is named for the episode takes S29E02's folder.
    /// </para>
    /// </remarks>
    [Fact]
    public void NeverTheShowsFolderAnotherEpisodesOrOneOutsideTheShow()
    {
        Assert.Null(Replacement.EncodedOf(S29E03, ShowFolder, [$"{Root}/South.Park.(1997)/South.Park.S29E03.mkv"]).Found);
        Assert.Null(Replacement.EncodedOf(S29E03, ShowFolder, [$"{Root}/South.Park.S29E03/South.Park.S29E03.Episode.3.NoMercy.m3u8"]).Found);
        Assert.Null(Replacement.EncodedOf(
            S29E03,
            ShowFolder,
            [$"{Root}/South.Park.(1997)/South.Park.S29E02/South.Park.S29E03.Episode.3.NoMercy.m3u8"]).Found);

        EncodedLookup none = Replacement.EncodedOf(S29E03, ShowFolder, [$"{Root}/South.Park.(1997)/South.Park.S29E03.mkv"]);

        Assert.False(string.IsNullOrWhiteSpace(none.Why));
    }

    /// <remarks>
    /// <strong>Two folders for one episode is not one to choose between.</strong> Where the library names the
    /// episode in two places the plugin cannot tell which it delivered, so it recycles neither. Sabotage:
    /// taking the first leaves it to the order the server answered in.
    /// </remarks>
    [Fact]
    public void TwoFoldersForOneEpisodeAreNeitherOfThem()
    {
        EncodedLookup lookup = Replacement.EncodedOf(
            S29E03,
            ShowFolder,
            [
                $"{Root}/South.Park.(1997)/South.Park.S29E03/South.Park.S29E03.Episode.3.NoMercy.m3u8",
                $"D:/elsewhere/South.Park.(1997)/South.Park.S29E03/South.Park.S29E03.Episode.3.NoMercy.m3u8",
            ]);

        Assert.Null(lookup.Found);
        Assert.Contains("more than one folder", lookup.Why, StringComparison.Ordinal);
    }
}

using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// Which files out of a finished torrent belong in the library.
/// </summary>
public class StagingTests
{
    /// <remarks>
    /// <strong>Only video files are written into a library folder.</strong> A
    /// rule from the working agreement rather than a preference: the subtitles,
    /// the NFO, the screenshots and the text file pointing at a website are
    /// somebody else's idea of what belongs on the owner's disk.
    /// </remarks>
    [Fact]
    public void OnlyVideoFilesAreEverStaged()
    {
        IReadOnlyList<Staged> staged = Staging.Choose(
            [
                File("Silo.S03E06.1080p.WEB-DL.x265.mkv", Gigabyte),
                File("Silo.S03E06.1080p.WEB-DL.x265.srt", 40_000),

                // As big as the episode, so that nothing but the extension
                // tells them apart: a scene release ships exactly this, and a
                // sample filter alone would let it through.
                File("silo.s03e06.rar", 3 * Gigabyte),
                File("Silo.S03E06.nfo", 900),
                File("RARBG.txt", 30),
                File("Screens/screen1.png", 400_000),
            ],
            [Episode(6)]);

        Staged only = Assert.Single(staged);

        Assert.Equal("Silo.S03E06.1080p.WEB-DL.x265.mkv", only.Path);
    }

    /// <remarks>
    /// The largest video, from docs/06-torrent-client.md. A release for one
    /// episode containing three videos has two the owner did not ask for.
    /// </remarks>
    [Fact]
    public void TheLargestVideoIsTheEpisode()
    {
        IReadOnlyList<Staged> staged = Staging.Choose(
            [
                File("extras/behind-the-scenes.mkv", 400 * Megabyte),
                File("Silo.S03E06.1080p.mkv", 3 * Gigabyte),
                File("extras/trailer.mp4", 90 * Megabyte),
            ],
            [Episode(6)]);

        Assert.Equal("Silo.S03E06.1080p.mkv", Assert.Single(staged).Path);
    }

    /// <remarks>
    /// A sample is the one thing in a torrent that is a real video file and
    /// must never be staged. An episode replaced by its own sample looks like a
    /// successful download until somebody presses play — and it is caught both
    /// by name and by size, because both happen.
    /// </remarks>
    [Fact]
    public void ASampleIsNeverTheEpisode()
    {
        Assert.Equal(
            "Silo.S03E06.1080p.mkv",
            Assert.Single(Staging.Choose(
                [
                    File("Sample/silo-sample.mkv", 60 * Megabyte),
                    File("Silo.S03E06.1080p.mkv", 3 * Gigabyte),
                ],
                [Episode(6)])).Path);

        // A small video that is not called a sample, beside a real episode, is
        // a clip and is not staged.
        Assert.Equal(
            "Silo.S03E06.1080p.mkv",
            Assert.Single(Staging.Choose(
                [File("Silo.S03E06.preview.mkv", 20 * Megabyte), File("Silo.S03E06.1080p.mkv", 3 * Gigabyte)],
                [Episode(6)])).Path);

        // A torrent that is nothing but a sample stages nothing at all, rather
        // than staging the sample because it was the largest.
        Assert.Empty(Staging.Choose([File("silo.sample.mkv", 20 * Megabyte)], [Episode(6)]));

        // But a small video that is the only video is the episode. A
        // twenty-minute anime at a low bitrate is smaller than the sample
        // threshold, and refusing it would leave that episode missing for ever.
        Assert.Equal(
            "[SubsPlease] Show - 137 (480p).mkv",
            Assert.Single(Staging.Choose(
                [File("[SubsPlease] Show - 137 (480p).mkv", 30 * Megabyte)],
                [Episode(6)])).Path);
    }

    /// <remarks>
    /// A season pack answers for several episodes, and each one is matched by
    /// its number in the file's own name. Never by order: a torrent lists its
    /// files however it likes, and staging episode four as episode one is worse
    /// than staging nothing.
    /// </remarks>
    [Fact]
    public void ASeasonPackYieldsOneFilePerEpisodeItCovers()
    {
        IReadOnlyList<Staged> staged = Staging.Choose(
            [
                File("Silo.S03/Silo.S03E03.1080p.mkv", 3 * Gigabyte),
                File("Silo.S03/Silo.S03E01.1080p.mkv", 3 * Gigabyte),
                File("Silo.S03/Silo.S03E02.1080p.mkv", 3 * Gigabyte),
                File("Silo.S03/Sample/sample.mkv", 40 * Megabyte),
                File("Silo.S03/Silo.S03.nfo", 200),
            ],
            [Episode(1), Episode(2), Episode(3)]);

        Assert.Equal(
            [(1, "Silo.S03/Silo.S03E01.1080p.mkv"), (2, "Silo.S03/Silo.S03E02.1080p.mkv"), (3, "Silo.S03/Silo.S03E03.1080p.mkv")],
            staged.Select(one => (one.Episode.Number, one.Path)));
    }

    /// <remarks>
    /// A pack missing an episode is worth saying so about. The episode stays
    /// missing and is looked for again, rather than being marked as having
    /// arrived because the pack it was in did.
    /// </remarks>
    [Fact]
    public void AnEpisodeNoFileAnsweredForIsNotQuietlyForgotten()
    {
        Staged[] files =
        [
            .. Staging.Choose(
                [File("Silo.S03E01.1080p.mkv", 3 * Gigabyte), File("Silo.S03E02.1080p.mkv", 3 * Gigabyte)],
                [Episode(1), Episode(2), Episode(3)]),
        ];

        Assert.Equal(2, files.Length);
        Assert.Equal([Episode(3)], Staging.Unanswered(files, [Episode(1), Episode(2), Episode(3)]));

        // And an episode present only as a twenty-megabyte clip beside
        // three-gigabyte ones is not answered for either. This is where size
        // has to decide: the clip carries that episode's number, so nothing but
        // its size next to its neighbours says it is not the episode.
        Staged[] withAClip =
        [
            .. Staging.Choose(
                [
                    File("Silo.S03E01.1080p.mkv", 3 * Gigabyte),
                    File("Silo.S03E02.1080p.mkv", 3 * Gigabyte),
                    File("Silo.S03E03.clip.mkv", 20 * Megabyte),
                ],
                [Episode(1), Episode(2), Episode(3)]),
        ];

        Assert.Equal([Episode(3)], Staging.Unanswered(withAClip, [Episode(1), Episode(2), Episode(3)]));
    }

    /// <remarks>
    /// A file from another season in the same torrent is not this season's
    /// episode, however its number reads.
    /// </remarks>
    [Fact]
    public void AFileFromAnotherSeasonIsNotThisSeasonsEpisode()
    {
        IReadOnlyList<Staged> staged = Staging.Choose(
            [File("Silo.S02E01.1080p.mkv", 3 * Gigabyte), File("Silo.S03E01.1080p.mkv", 3 * Gigabyte)],
            [Episode(1), Episode(2)]);

        Assert.Equal("Silo.S03E01.1080p.mkv", Assert.Single(staged).Path);
    }

    /// <remarks>
    /// A torrent with no video in it at all stages nothing, and says nothing
    /// arrived — rather than throwing on the way past.
    /// </remarks>
    [Fact]
    public void ATorrentWithNoVideoInItStagesNothing()
    {
        Assert.Empty(Staging.Choose([File("readme.txt", 100), File("cover.jpg", 90_000)], [Episode(6)]));
        Assert.Empty(Staging.Choose([], [Episode(6)]));
    }

    private const long Megabyte = 1024 * 1024;

    private const long Gigabyte = 1024 * Megabyte;

    private static TorrentFile File(string path, long length)
    {
        return new(path, length);
    }

    private static EpisodeKey Episode(int number)
    {
        return new(42, 3, number);
    }
}

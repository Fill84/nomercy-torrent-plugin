using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
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

    /// <remarks>
    /// <para>
    /// <strong>A torrent added by hand answers for whatever it turns out to
    /// hold.</strong> docs/08-ui.md § Actions: <c>AddTorrent</c> still runs the
    /// finished file through staging and the encode dispatch, because a torrent
    /// added by hand is an episode like any other.
    /// </para>
    /// <para>
    /// It is recorded covering no episode, and deliberately so: claiming one
    /// nobody chose would put that episode back to missing if the download
    /// failed. So the episodes are read out of the torrent itself once it is
    /// finished, which is what this does. Without it a season pack added by
    /// hand downloaded in full and stopped there — on 30 August 2026, 37 GB of
    /// Dark Matter sat complete in the download folder with nothing to move it,
    /// because staging is handed the episodes and there were none.
    /// </para>
    /// </remarks>
    [Fact]
    public void APackAddedByHandIsReadBackIntoTheEpisodesItHolds()
    {
        IReadOnlyList<EpisodeKey> found = Staging.Discover(
            [
                File("Dark.Matter.2024.S01E01.Are.You.Happy.in.Your.Life.1080p.ATVP.WEB-DL.H.264-FLUX.mkv", Gigabyte),
                File("Dark.Matter.2024.S01E02.Trip.of.a.Lifetime.1080p.ATVP.WEB-DL.H.264-FLUX.mkv", Gigabyte),
                File("Dark.Matter.2024.S01E03.The.Box.1080p.ATVP.WEB-DL.H.264-FLUX.mkv", Gigabyte),

                // Everything a pack ships beside the episodes, none of which is
                // an episode of anything.
                File("NEW upcoming releases by Xclusive.txt", 71),
                File("[TGx]Downloaded from torrentgalaxy.to .txt", 479),
            ],
            [DarkMatter, SomethingElse]);

        Assert.Equal(
            [new(7, 1, 1), new(7, 1, 2), new(7, 1, 3)],
            found);
    }

    /// <remarks>
    /// One episode added by hand is the same thing with one file. The single
    /// magnet is the ordinary case — a pack is the harder one — and both go the
    /// same way.
    /// </remarks>
    [Fact]
    public void OneEpisodeAddedByHandIsFoundTheSameWay()
    {
        Assert.Equal(
            [new(9, 3, 6)],
            Staging.Discover([File("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", Gigabyte)], [DarkMatter, SomethingElse]));
    }

    /// <remarks>
    /// A show the owner does not have is not guessed at. Staging it would put a
    /// file in somebody else's library folder, and that is worse than leaving it
    /// in the download folder where the owner put it.
    /// </remarks>
    [Fact]
    public void AShowTheOwnerDoesNotHaveIsNotStagedAtAll()
    {
        Assert.Empty(
            Staging.Discover(
                [File("Some.Show.Nobody.Has.S01E01.1080p.WEB.H264.mkv", Gigabyte)],
                [DarkMatter, SomethingElse]));

        // And a video that names no episode at all is not an episode, however
        // much it looks like one.
        Assert.Empty(Staging.Discover([File("Dark.Matter.2024.1080p.mkv", Gigabyte)], [DarkMatter]));
    }

    /// <remarks>
    /// The same episode in two files is one episode. A pack that ships a repack
    /// beside the original would otherwise be staged twice, and the second
    /// dispatch overwrites what the first one encoded.
    /// </remarks>
    [Fact]
    public void TheSameEpisodeTwiceIsStillOneEpisode()
    {
        Assert.Equal(
            [new(7, 1, 1)],
            Staging.Discover(
                [
                    File("Dark.Matter.2024.S01E01.1080p.ATVP.WEB-DL.H.264-FLUX.mkv", Gigabyte),
                    File("Dark.Matter.2024.S01E01.REPACK.1080p.ATVP.WEB-DL.H.264-FLUX.mkv", Gigabyte),
                ],
                [DarkMatter]));
    }

    /// <remarks>
    /// <para>
    /// The owner's own pack, file for file, taken off the download folder on
    /// 30 August 2026 — nine episodes and the two text files a release ships
    /// with. Every other test here names its files by hand, and a name written
    /// by the person who also wrote the parser proves nothing about what a
    /// scene group actually publishes.
    /// </para>
    /// <para>
    /// Episode titles are the trap. <c>Are.You.Happy.in.Your.Life</c> and
    /// <c>In.the.Fires.of.Dead.Stars</c> are words where a parser expects tags,
    /// and a season pack whose episode four is filed as episode one is worse
    /// than a pack that stages nothing at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheOwnersOwnSeasonPackIsReadIntoItsNineEpisodes()
    {
        IReadOnlyList<TorrentFile> pack =
        [
            .. Capture.Fixture("dark-matter-s01-pack.txt")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)

                // Roughly four gigabytes each, which is what they are. The
                // text files are bytes, and the sample rule turns on size.
                .Select(path => new TorrentFile(path, path.EndsWith(".mkv", StringComparison.Ordinal) ? 4 * Gigabyte : 500)),
        ];

        IReadOnlyList<EpisodeKey> found = Staging.Discover(pack, [DarkMatter, SomethingElse]);

        Assert.Equal(
            [
                new(7, 1, 1), new(7, 1, 2), new(7, 1, 3), new(7, 1, 4), new(7, 1, 5),
                new(7, 1, 6), new(7, 1, 7), new(7, 1, 8), new(7, 1, 9),
            ],
            found);

        // And the whole way through: each episode gets the file that names it,
        // and neither text file is staged.
        IReadOnlyList<Staged> chosen = Staging.Choose(pack, found);

        Assert.Equal(9, chosen.Count);
        Assert.All(chosen, one => Assert.EndsWith(".mkv", one.Path, StringComparison.Ordinal));

        foreach (Staged one in chosen)
        {
            Assert.Contains($"S01E{one.Episode.Number:00}", one.Path, StringComparison.Ordinal);
        }

        // Nothing is left over, which is what says the pack arrived whole.
        Assert.Empty(Staging.Unanswered(chosen, found));
    }

    /// <summary>A show the owner has, in a tv library.</summary>
    private static Show DarkMatter =>
        new(7, "Dark Matter", 2024, "01HQ5W4AVF30N10RT6XCF6AJHM", "Television", LibraryKind.Television, "Dark Matter (2024)");

    /// <summary>Another, so that matching has to choose rather than take the only one.</summary>
    private static Show SomethingElse =>
        new(9, "Silo", 2023, "01HQ5W4AVF30N10RT6XCF6AJHM", "Television", LibraryKind.Television, "Silo (2023)");

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

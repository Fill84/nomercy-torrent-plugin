using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>Which files of a season pack are downloaded, and which episodes it turns out to hold.</summary>
/// <remarks>
/// <para>
/// <c>docs/specs/release-names.md</c> § Season packs, GitHub issue #1: once its metadata is in, the client
/// downloads only the video files that name an episode the grab answers for — an episode with a file in the
/// library is not one of them — and an episode the pack turns out not to hold stays missing.
/// </para>
/// <para>
/// The files are the owner's own Dark Matter season pack, file for file: nine episodes and two text files.
/// </para>
/// </remarks>
public sealed class SeasonPackFilesTests
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    /// <remarks>
    /// Episodes three and five are searched for; the other seven have their files. Only their two videos are
    /// downloaded.
    /// </remarks>
    [Fact]
    public void OnlyTheVideosOfTheEpisodesTheGrabAnswersForAreDownloaded()
    {
        IReadOnlyList<TorrentFile> kept = Staging.ForEpisodes(Pack(), [Episode(3), Episode(5)]);

        Assert.Equal(
            [
                "Dark.Matter.2024.S01E03.The.Box.1080p.ATVP.WEB-DL.DDP5.1.Atmos.H.264-FLUX.mkv",
                "Dark.Matter.2024.S01E05.Worldless.1080p.ATVP.WEB-DL.DDP5.1.Atmos.H.264-FLUX.mkv",
            ],
            kept.Select(file => file.Path).Order(StringComparer.Ordinal));
    }

    /// <remarks>
    /// <para>
    /// <strong>A pack the owner adds by hand, 19 November 2026.</strong> It was taken for no episode, so every
    /// video in it was downloaded, the ones the library already had among them. The episodes the library
    /// holds are left out; a video naming no episode is nobody's to leave out, and stays.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheVideosOfEpisodesTheLibraryAlreadyHasAreNotDownloaded()
    {
        IReadOnlyList<TorrentFile> pack = [.. Pack(), new TorrentFile("Dark.Matter.2024.Featurette.1080p.mkv", Gigabyte)];

        IReadOnlyList<TorrentFile> kept = Staging.NotAlready(
            pack,
            [Episode(1), Episode(2), Episode(4), Episode(6), Episode(7), Episode(8), Episode(9)]);

        Assert.Equal(
            [
                "Dark.Matter.2024.Featurette.1080p.mkv",
                "Dark.Matter.2024.S01E03.The.Box.1080p.ATVP.WEB-DL.DDP5.1.Atmos.H.264-FLUX.mkv",
                "Dark.Matter.2024.S01E05.Worldless.1080p.ATVP.WEB-DL.DDP5.1.Atmos.H.264-FLUX.mkv",
            ],
            kept.Select(file => file.Path).Order(StringComparer.Ordinal));
    }

    /// <remarks>
    /// The same season number in another show's pack is not this show's episode: only a torrent named for a
    /// show in a library has episodes to leave out, and it is found by the name the torrent gives itself.
    /// </remarks>
    [Fact]
    public void TheShowATorrentIsNamedForIsFoundByItsOwnName()
    {
        Show[] shows =
        [
            new(7, "Dark Matter", 2024, "library", "Series", LibraryKind.Television, "Dark.Matter.(2024)"),
            new(8, "The Ark", 2023, "library", "Series", LibraryKind.Television, "The.Ark.(2023)"),
        ];

        Assert.Equal(8, Staging.ShowNamed("The Ark (2023) S03 (1080p AMZN WEB-DL x265 10bit EAC3 5.1 Celdra)", shows)?.Id);
        Assert.Equal(7, Staging.ShowNamed("Dark.Matter.2024.S01.COMPLETE.1080p.ATVP.WEB-DL.DDP5.1.Atmos.H.264-FLUX[TGx]", shows)?.Id);
        Assert.Null(Staging.ShowNamed("Silo.S03.1080p.WEB.H264-CAKES", shows));
        Assert.Null(Staging.ShowNamed(null, shows));
    }

    /// <remarks>
    /// The pack holds nine episodes; a grab answering for episode ten as well learns from its files that it does
    /// not hold that one.
    /// </remarks>
    [Fact]
    public void AnEpisodeThePackDoesNotHoldIsNotAmongWhatItHolds()
    {
        Assert.Equal([Episode(3)], Staging.Held(Pack(), [Episode(3), Episode(10)]));
    }

    /// <remarks>
    /// A pack left answering for one episode stages the file that names it. One cover used to mean "the largest
    /// video", which in a pack of nine equal episodes is whichever the torrent lists first — and the only one of
    /// them downloaded is episode five's.
    /// </remarks>
    [Fact]
    public void APackAnsweringForOneEpisodeStagesTheFileThatNamesIt()
    {
        Staged staged = Assert.Single(Staging.Choose(Pack(), [Episode(5)]));

        Assert.Contains("S01E05", staged.Path, StringComparison.Ordinal);
    }

    private static EpisodeKey Episode(int number)
    {
        return new(7, 1, number);
    }

    private static IReadOnlyList<TorrentFile> Pack()
    {
        return
        [
            .. Capture.Fixture("dark-matter-s01-pack.txt")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)

                // Roughly four gigabytes each, which is what they are. The text files are bytes, and the sample
                // rule turns on size.
                .Select(path => new TorrentFile(path, path.EndsWith(".mkv", StringComparison.Ordinal) ? 4 * Gigabyte : 500)),
        ];
    }
}

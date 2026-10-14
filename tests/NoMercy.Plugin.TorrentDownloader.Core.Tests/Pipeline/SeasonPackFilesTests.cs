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

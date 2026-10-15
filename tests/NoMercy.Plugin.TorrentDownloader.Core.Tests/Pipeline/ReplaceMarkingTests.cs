using Microsoft.Extensions.Time.Testing;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// Which episodes the plugin delivered from a release no name source names, and so replaces on its own.
/// </summary>
/// <remarks>
/// The owner's rule of 15 October 2026 (<c>docs/specs/run.md</c> § When a download finishes). South Park
/// S29E03 was delivered that morning from <c>South Park S29E03 1080p HDTV DD 2.0 x264-ViETNAM</c>, grab 99 on
/// the owner's server, found by asking the indexers for the show and episode three hours before any name
/// source named <c>South.Park.S29E03.1080p.WEB.H264-CAKES</c>.
/// </remarks>
public class ReplaceMarkingTests
{
    private const string Vietnam = "South Park S29E03 1080p HDTV DD 2.0 x264-ViETNAM";

    private const string VietnamHash = "7445E4910B7A4D7E1F3C35D5A4A9C2E1B0C0FFEE";

    private static readonly DateTimeOffset Today = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Aired = new(2026, 10, 15);

    private static readonly EpisodeKey SouthPark = new(2190, 29, 3);

    private static readonly EpisodeKey Frieren = new(5, 1, 1);

    /// <remarks>
    /// <para>
    /// <strong>A show's episode the plugin delivered from a release no source names is to be replaced.</strong>
    /// It has a file, so it is not missing; it is tracked to be replaced, with the delivery it replaces named.
    /// </para>
    /// <para>
    /// Sabotage: MissingRefresh skipping every episode with a file, as it did before this rule, tracks nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AShowsEpisodeDeliveredFromAReleaseNoSourceNamesIsToBeReplaced()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(2190, "South Park", 1997)
            .Episode(2190, 29, 3, airDate: Aired, hasFile: true);

        IReadOnlyList<TrackedEpisode> tracked = await Derive(library, [Delivered(SouthPark, named: false)]);

        TrackedEpisode marked = Assert.Single(tracked);

        Assert.Equal(SouthPark, marked.Key);
        Assert.Equal(EpisodeState.Replace, marked.State);
        Assert.Equal(new Replacing(VietnamHash, Vietnam), marked.Replaces);
    }

    /// <remarks>
    /// <strong>An anime is never marked</strong>, whatever its release: fansubs are its real releases and no
    /// name source lists them, so an anime grab found by show and episode is what an anime is meant to get.
    /// Sabotage: marking without asking the show's kind marks Frieren.
    /// </remarks>
    [Fact]
    public async Task AnAnimesEpisodeIsNeverToBeReplaced()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(5, "Frieren", 2023, LibraryKind.Anime, "lib-anime", "Anime")
            .Episode(5, 1, 1, airDate: Aired, hasFile: true);

        Assert.Empty(await Derive(library, [Delivered(Frieren, named: false, release: "[SubsPlease] Frieren - 01 (1080p)")]));
    }

    /// <remarks>
    /// <strong>A delivery whose release a name source gave is never marked</strong>, and neither is one whose
    /// release has not been checked yet: a grab from before this version is checked against the name sources
    /// first (<c>DeliveredCheck</c>), and until then nothing is known against it. Sabotage: marking on
    /// anything other than a release known not to be named marks both.
    /// </remarks>
    [Fact]
    public async Task ADeliveryWhoseReleaseASourceNamedOrIsNotYetCheckedIsNeverToBeReplaced()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(2190, "South Park", 1997)
            .Episode(2190, 29, 3, airDate: Aired, hasFile: true)
            .Episode(2190, 29, 2, airDate: Aired, hasFile: true);

        IReadOnlyList<TrackedEpisode> tracked = await Derive(
            library,
            [
                Delivered(SouthPark, named: true, release: "South.Park.S29E03.1080p.WEB.H264-CAKES"),
                Delivered(new(2190, 29, 2), named: null, release: "South.Park.S29E02.1080p.WEB.H264-CAKES", hash: "AB" + VietnamHash[2..]),
            ]);

        Assert.Empty(tracked);
    }

    /// <remarks>
    /// <para>
    /// <strong>The latest delivery decides.</strong> Once the replacement is done it is the latest delivery
    /// of the episode, its release is named, and the episode is no longer to be replaced — even though the
    /// ViETNAM grab is still in the store.
    /// </para>
    /// <para>
    /// <strong>And an episode the owner put in the library is never marked</strong>: no grab of the plugin's
    /// delivered it, so there is nothing of the plugin's to replace. Nor is one added by hand, which is the
    /// owner's own choice of release.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheLatestDeliveryDecidesAndAnEpisodeTheOwnerPutThereIsNeverMarked()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(2190, "South Park", 1997)
            .Episode(2190, 29, 3, airDate: Aired, hasFile: true)
            .Episode(2190, 29, 1, airDate: Aired, hasFile: true)
            .Episode(2190, 29, 2, airDate: Aired, hasFile: true);

        IReadOnlyList<TrackedEpisode> tracked = await Derive(
            library,
            [
                Delivered(SouthPark, named: false) with { GrabbedAt = Today.AddHours(-9) },
                Delivered(SouthPark, named: true, release: "South.Park.S29E03.1080p.WEB.H264-CAKES", hash: "CD" + VietnamHash[2..]) with
                {
                    GrabbedAt = Today.AddHours(-3),
                },
                Delivered(new(2190, 29, 2), named: false, release: "South Park S29E02 by hand", hash: "EF" + VietnamHash[2..]) with
                {
                    ByHand = true,
                },
            ]);

        Assert.Empty(tracked);
    }

    private static StoredDownload Delivered(EpisodeKey episode, bool? named, string release = Vietnam, string hash = VietnamHash)
    {
        return new(hash, $"magnet:?xt=urn:btih:{hash}", release, GrabState.Done)
        {
            Covers = [episode],
            Named = named,
            GrabbedAt = Today.AddHours(-9),
        };
    }

    private static async Task<IReadOnlyList<TrackedEpisode>> Derive(FakeLibrary library, IReadOnlyList<StoredDownload> delivered)
    {
        return await new MissingRefresh(library, FakeAppliedSettings.EveryShowOn(), new FakeTimeProvider(Today), delivered)
            .DeriveAsync(CancellationToken.None);
    }
}

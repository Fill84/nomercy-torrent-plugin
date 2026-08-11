// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Orchestration;
using NoMercy.Plugin.TorrentDownloader.Core.Profiles;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Indexers;

/// <summary>
/// The whole chain, on one real page, with the owner's real settings.
///
/// <para>
/// Every part of this was covered on its own and nothing downloaded for a fortnight. The
/// parser was tested against invented markup that had a magnet in it; the decider was tested
/// against releases somebody typed. Both passed, and the two of them together could not get
/// a single episode off a site that was answering correctly the whole time.
/// </para>
///
/// <para>
/// So this one runs the real capture through every step in order - fetch, parse, name, match,
/// score, choose - and asserts on the thing the owner actually wants: a magnet for the
/// episode they are missing. When it breaks it names the step that broke.
/// </para>
/// </summary>
public class RealSiteChainTests
{
    /// <summary>The owner's own settings on 11 August 2026, not a convenient set.</summary>
    private static ReleaseProfile OwnersProfile() => new()
    {
        Name = "default",
        Quality = QualityLadders.UpTo(Resolution.Fhd1080),
        MinSeeders = 2,
        AllowSeasonPacks = true,
    };

    private static readonly string[] Trackers = ["udp://tracker.example:1337/announce"];

    private static WantedEpisode Silo() => new()
    {
        Key = new EpisodeKey(125988, 3, 4),
        ShowTitle = "Silo",
        AirDate = new DateOnly(2026, 7, 23),
    };

    private static async Task<IReadOnlyList<ReleaseInfo>> SearchAsync()
    {
        SiteIndexer indexer = new(
            "limetorrents",
            25,
            "https://www.limetorrents.fun/search/tv/{query}/",
            new ChallengeAwareFetch(
                new HttpClient(StubHttpMessageHandler.Returning(
                    Fixtures.Text("limetorrents-search.html"),
                    contentType: "text/html")),
                new ClearanceStore(() => DateTimeOffset.UtcNow)),

            // A swarm to ask. Named here rather than taken from the settings type, which
            // lives in the shell: this assembly is the part that needs no host to test.
            // The value does not matter; that there is one is the failure this proves gone.
            Trackers);

        return await indexer.SearchAsync(
            new SearchQuery("Silo", new EpisodeSlot(3, 4)),
            CancellationToken.None);
    }

    [Fact]
    public async Task Step1_TheSiteYieldsReleases()
    {
        (await SearchAsync()).Should().NotBeEmpty("the page holds four releases for this episode");
    }

    [Fact]
    public async Task Step2_EveryReleaseHasSomewhereToGetItFrom()
    {
        (await SearchAsync()).Should().OnlyContain(release => release.MagnetUri != null);
    }

    /// <summary>
    /// And somewhere to find people who have it. A magnet built from a hash alone has only
    /// DHT behind it, and on a real swarm DHT alone answered nobody: five minutes of asking
    /// and then a MetadataException, every cycle, for a fortnight.
    /// </summary>
    [Fact]
    public async Task Step2b_EveryBuiltMagnetNamesASwarmToAsk()
    {
        (await SearchAsync()).Should().OnlyContain(
            release => release.MagnetUri!.Contains("&tr="));
    }

    /// <summary>
    /// The minimum the owner set is two. A release read as zero-seeded is refused, so this
    /// is the step that decides whether any of the rest matters.
    /// </summary>
    [Fact]
    public async Task Step3_TheSeederCountsSurvivedTheMarkup()
    {
        (await SearchAsync()).Should().OnlyContain(release => release.Seeders >= 2);
    }

    /// <summary>
    /// The name has to parse back to the slot that was asked for, or the decider refuses it
    /// however good the release is.
    /// </summary>
    [Fact]
    public async Task Step4_TheNamesParseToTheEpisodeThatWasAskedFor()
    {
        IReadOnlyList<ReleaseInfo> found = await SearchAsync();

        found.Select(release => ReleaseNameParser.ParseEpisode(release.Title))
            .Should().Contain(slot => slot != null && slot.Value.Season == 3 && slot.Value.Episode == 4);
    }

    [Fact]
    public async Task Step5_TheTitlesMatchTheShow()
    {
        (await SearchAsync()).Should().Contain(release => TitleMatcher.Matches(release.Title, "Silo"));
    }

    /// <summary>
    /// The end of the chain, and the only assertion that matters: the owner is missing Silo
    /// S03E04, the site has it, and this is the plugin deciding to take it.
    /// </summary>
    [Fact]
    public async Task Step6_TheDeciderPicksOneAndItHasAMagnet()
    {
        ProfileReleaseChooser chooser = new(OwnersProfile());

        ReleaseInfo? chosen = chooser.Choose(Silo(), await SearchAsync(), allowSeasonPacks: false);

        chosen.Should().NotBeNull("the site is offering exactly the episode the library is missing");
        chosen!.MagnetUri.Should().StartWith("magnet:?xt=urn:btih:");
        TitleMatcher.Matches(chosen.Title, "Silo").Should().BeTrue();
    }

    /// <summary>
    /// The owner asked for nothing above 1080p. A 2160p release is on this page, and taking
    /// it would be the plugin overruling a setting rather than honouring it.
    /// </summary>
    [Fact]
    public async Task Step7_ItRespectsTheQualityCeiling()
    {
        ProfileReleaseChooser chooser = new(OwnersProfile());

        ReleaseInfo chosen = (await SearchAsync()) is { Count: > 0 } found
            ? chooser.Choose(Silo(), found, allowSeasonPacks: false)!
            : throw new InvalidOperationException("the search found nothing");

        Resolution taken = ReleaseNameParser.Parse(chosen.Title).Quality.Resolution;

        ((int)taken).Should().BeLessThanOrEqualTo((int)Resolution.Fhd1080);
    }
}

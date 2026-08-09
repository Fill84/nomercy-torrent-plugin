// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Orchestration;
using NoMercy.Plugin.TorrentDownloader.Core.Profiles;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Orchestration;

/// <summary>
/// The orchestrator decides whether a season's bytes are worth spending; this decides
/// what that answer does to the candidates. The orchestrator's own tests can only watch
/// the flag being passed, so the flag meaning something is proved here.
/// </summary>
public class ProfileReleaseChooserTests
{
    private static readonly ReleaseProfile Profile = new()
    {
        Name = "test",
        Quality = new QualityLadder([new QualityDefinition("WEB-1080p", Resolution.Fhd1080, ReleaseSource.Unknown)], "WEB-1080p"),
        AllowSeasonPacks = true,
        MinSeeders = 1,
    };

    private static readonly WantedEpisode Episode = new()
    {
        Key = new EpisodeKey(1, 1, 2),
        ShowTitle = "Some Show",
    };

    private static ReleaseInfo Release(string title) => new()
    {
        IndexerName = "site-a",
        TorrentId = title,
        Title = title,
        InfoHash = "abc123",
        MagnetUri = "magnet:?xt=urn:btih:abc123",
        SizeBytes = 2_000_000_000,
        Seeders = 40,
        Trackers = [],
    };

    [Fact]
    public void Choose_TakesTheSeasonPackWhenTheCallerAllowsIt()
    {
        ProfileReleaseChooser chooser = new(Profile);

        ReleaseInfo? chosen = chooser.Choose(Episode, [Release("Some.Show.S01.1080p.WEB-DL")], allowSeasonPacks: true);

        chosen.Should().NotBeNull();
    }

    [Fact]
    public void Choose_RefusesTheSeasonPackWhenTheCallerDoesNot()
    {
        ProfileReleaseChooser chooser = new(Profile);

        ReleaseInfo? chosen = chooser.Choose(Episode, [Release("Some.Show.S01.1080p.WEB-DL")], allowSeasonPacks: false);

        // Refused rather than fallen back on: the whole candidate list was one pack, so a
        // chooser that ignored the flag would hand it back and the caller's arithmetic
        // about a season's bytes would count for nothing.
        chosen.Should().BeNull();
    }

    [Fact]
    public void Choose_StillTakesTheEpisodeReleaseWhenPacksAreRefused()
    {
        ProfileReleaseChooser chooser = new(Profile);

        ReleaseInfo? chosen = chooser.Choose(
            Episode,
            [Release("Some.Show.S01.1080p.WEB-DL"), Release("Some.Show.S01E02.1080p.WEB-DL")],
            allowSeasonPacks: false);

        chosen!.Title.Should().Be("Some.Show.S01E02.1080p.WEB-DL");
    }

    // The profile is the owner's standing answer and the caller's flag is this search's.
    // Either one saying no is a no, or turning packs off in the profile would be undone
    // by a season with enough gaps in it.
    [Fact]
    public void Choose_RefusesAPackTheProfileForbidsEvenWhenTheCallerWouldAllowIt()
    {
        ProfileReleaseChooser chooser = new(Profile with { AllowSeasonPacks = false });

        ReleaseInfo? chosen = chooser.Choose(Episode, [Release("Some.Show.S01.1080p.WEB-DL")], allowSeasonPacks: true);

        chosen.Should().BeNull();
    }
}

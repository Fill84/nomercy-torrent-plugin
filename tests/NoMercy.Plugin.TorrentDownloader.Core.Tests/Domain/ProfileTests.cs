using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Domain;

public class ProfileTests
{
    /// <remarks>
    /// Every default here is written in docs/04-domain.md § Settings. A default
    /// that quietly differs is a plugin behaving unlike its own documentation,
    /// which is how an owner comes to distrust both.
    /// </remarks>
    [Fact]
    public void TheProfileCarriesItsDocumentedDefaults()
    {
        Profile profile = new();

        Assert.False(profile.IncludeSpecials);
        Assert.Equal("1080p", profile.MaximumResolution);
        Assert.Equal(Profile.AnyCodec, profile.Codec);
        Assert.True(profile.RequireCodecTag);
        Assert.True(profile.EnglishOnly);
        Assert.Empty(profile.ExcludeTerms);
        Assert.Equal(2, profile.MinimumSeeders);
        Assert.True(profile.AllowSeasonPacks);
        Assert.Equal(3, profile.SeasonPackThreshold);
        Assert.Equal(3, profile.MaxSearchAttempts);
    }

    /// <remarks>
    /// "Require the codec tag" only means anything once a codec is named: with
    /// no codec wanted, refusing every untagged release would refuse most of
    /// what the feeds carry and the owner would see an empty queue with no
    /// reason given.
    /// </remarks>
    [Fact]
    public void RequiringACodecTagOnlyBitesWhenACodecIsNamed()
    {
        Assert.False(new Profile { Codec = Profile.AnyCodec }.CodecTagRequired);
        Assert.True(new Profile { Codec = "h265" }.CodecTagRequired);
        Assert.False(new Profile { Codec = "h265", RequireCodecTag = false }.CodecTagRequired);
    }
}

public class ClientLimitsTests
{
    [Fact]
    public void TheClientLimitsCarryTheirDocumentedDefaults()
    {
        ClientLimits limits = new();

        Assert.Equal(5, limits.MaxConcurrentDownloads);
        Assert.Equal(51413, limits.ListenPort);
        Assert.True(limits.PortMapping);
        Assert.Equal(0, limits.MaxDownloadRate);
        Assert.Equal(0, limits.MaxUploadRate);
        Assert.Equal(1.0, limits.SeedRatio);
        Assert.Equal(48, limits.SeedHours);
        Assert.Equal(30, limits.StallMinutes);
        Assert.Equal(5, limits.MetadataTimeoutMinutes);
        Assert.Equal(EncryptionPolicy.Allowed, limits.Encryption);
    }

    /// <remarks>
    /// Empty, and deliberately so: docs/04-domain.md says "a shipped list" and
    /// no document anywhere says which trackers are in it. Shipping a list
    /// nobody chose would have this plugin announcing itself to hosts the owner
    /// never agreed to.
    /// </remarks>
    [Fact]
    public void NoTrackerIsShippedUntilSomebodyChoosesOne()
    {
        Assert.Empty(new ClientLimits().DefaultTrackers);
    }
}

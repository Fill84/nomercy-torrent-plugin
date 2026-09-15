using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Storage;

/// <summary>
/// The settings the owner saved per show and per library, against a real SQLite file.
/// </summary>
/// <remarks>
/// Read back through a store opened afresh, which is what a restart is: settings the owner saved on
/// the overview page and lost to a restart would be a show that quietly stops downloading.
/// </remarks>
public class ShowSettingsRepositoryTests : IAsyncLifetime
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        TemporaryFolder.Forget(_folder);

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ASavedShowReadsBackAsItWasSaved()
    {
        ShowSettings saved = new(41)
        {
            SwitchedOn = true,
            Quality = "2160p",
            Codec = "h265",
            Specials = true,
            Wishes = ["WEB", "NTb"],
            Musts = ["AMZN"],
            Forbidden = ["HDR", "MULTi"],
        };

        await new ShowSettingsRepository(await Opened()).SaveAsync(saved, CancellationToken.None);

        ShowSettings read = await new ShowSettingsRepository(await Opened()).ForAsync(41, CancellationToken.None);

        Assert.True(read.SwitchedOn);
        Assert.True(read.Saved);
        Assert.Equal("2160p", read.Quality);
        Assert.Equal("h265", read.Codec);
        Assert.True(read.Specials);
        Assert.Equal(["WEB", "NTb"], read.Wishes);
        Assert.Equal(["AMZN"], read.Musts);
        Assert.Equal(["HDR", "MULTi"], read.Forbidden);
    }

    /// <remarks>
    /// Following the library is a value of its own and has to survive the trip: a show saved as
    /// "follow the library" that read back with a quality of its own would stop following it.
    /// </remarks>
    [Fact]
    public async Task AShowThatFollowsItsLibraryReadsBackFollowingIt()
    {
        await new ShowSettingsRepository(await Opened()).SaveAsync(new(41) { SwitchedOn = true }, CancellationToken.None);

        ShowSettings read = await new ShowSettingsRepository(await Opened()).ForAsync(41, CancellationToken.None);

        Assert.Null(read.Quality);
        Assert.Null(read.Codec);
        Assert.Null(read.Specials);
        Assert.Empty(read.Wishes);
    }

    [Fact]
    public async Task AShowNobodyTouchedIsOffAndNotSaved()
    {
        ShowSettings read = await new ShowSettingsRepository(await Opened()).ForAsync(41, CancellationToken.None);

        Assert.False(read.SwitchedOn);
        Assert.False(read.Saved);
    }

    /// <remarks>
    /// <c>docs/specs/show-list.md</c>: a show switched off has nothing searched and its saved settings
    /// are kept; and the overview's row button switches a show on without saving it.
    /// </remarks>
    [Fact]
    public async Task SwitchingAShowKeepsWhatWasSavedAndSavesNothing()
    {
        ShowSettingsRepository shows = new(await Opened());

        await shows.SaveAsync(new(41) { SwitchedOn = true, Quality = "720p", Forbidden = ["HDR"] }, CancellationToken.None);
        await shows.SwitchAsync(41, on: false, CancellationToken.None);

        ShowSettings off = await shows.ForAsync(41, CancellationToken.None);

        Assert.False(off.SwitchedOn);
        Assert.True(off.Saved);
        Assert.Equal("720p", off.Quality);
        Assert.Equal(["HDR"], off.Forbidden);

        await shows.SwitchAsync(52, on: true, CancellationToken.None);

        ShowSettings switchedOnly = await shows.ForAsync(52, CancellationToken.None);

        Assert.True(switchedOnly.SwitchedOn);
        Assert.False(switchedOnly.Saved);
    }

    [Fact]
    public async Task EveryShowTheOwnerTouchedIsReadAtOnce()
    {
        ShowSettingsRepository shows = new(await Opened());

        await shows.SaveAsync(new(41) { SwitchedOn = true }, CancellationToken.None);
        await shows.SwitchAsync(52, on: true, CancellationToken.None);

        IReadOnlyDictionary<int, ShowSettings> all = await shows.AllAsync(CancellationToken.None);

        Assert.Equal([41, 52], all.Keys.Order());
        Assert.True(all[41].Saved);
        Assert.False(all[52].Saved);
    }

    [Fact]
    public async Task PreferencesReadBackAsTheyWereSaved()
    {
        LibraryPreferences saved = new("01HQ5W4AVF30N10RT6XCF6AJHM")
        {
            Quality = "1080p",
            Codec = "h264",
            Specials = true,
            Wishes = ["WEB"],
            Musts = ["H264"],
            Forbidden = ["DUAL"],
        };

        await new LibraryPreferencesRepository(await Opened()).SaveAsync(saved, CancellationToken.None);

        LibraryPreferences read = await new LibraryPreferencesRepository(await Opened())
            .ForAsync("01HQ5W4AVF30N10RT6XCF6AJHM", CancellationToken.None);

        Assert.Equal("1080p", read.Quality);
        Assert.Equal("h264", read.Codec);
        Assert.True(read.Specials);
        Assert.Equal(["WEB"], read.Wishes);
        Assert.Equal(["H264"], read.Musts);
        Assert.Equal(["DUAL"], read.Forbidden);
    }

    [Fact]
    public async Task ALibraryNobodyTouchedReadsBackUntouched()
    {
        LibraryPreferences read = await new LibraryPreferencesRepository(await Opened())
            .ForAsync("01HQ5W4AVF30N10RT6XCF6AJHM", CancellationToken.None);

        Assert.Null(read.Quality);
        Assert.Equal(LibraryPreferences.AnyCodec, read.Codec);
        Assert.False(read.Specials);
    }

    private async Task<Store> Opened()
    {
        Store database = new(_folder);
        await database.MigrateAsync(CancellationToken.None);

        return database;
    }
}

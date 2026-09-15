using Microsoft.Extensions.Time.Testing;

using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

/// <summary>
/// Which shows are searched for and kept downloading is the switch on the overview, read from the
/// plugin's own settings, and both halves of the plugin answer it the same way.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/specs/show-list.md</c> § Switching a show on. <c>MissingRefresh</c> decides which episodes are
/// searched for; the transfers pass cancels a download whose show is not. They are one policy: while it
/// was two, the plugin could grab a show and cancel it on the next tick.
/// </para>
/// <para>
/// Until 15 September 2026 that policy was <c>Ownership</c> — a show with a file on disk — because the
/// server keeps rows for shows nobody added, and taking every show put the owner on 479 grabs in an
/// afternoon. The switch replaces it: nothing is searched that the owner did not switch on, so a show
/// with nothing on disk is safe to search once they have.
/// </para>
/// </remarks>
public sealed class TheSwitchDecidesTests : IDisposable
{
    private const string TelevisionLibrary = "01KZGKX2G0966V80H26EKGG5T0";

    private const string AnimeLibrary = "01KZGKX2G0966V80H26EKGG5T9";

    private const int Silo = 41;

    private const int FamilyGuy = 99;

    private const int Severance = 52;

    private const int Andor = 63;

    private const string SiloHash = "0123456789ABCDEF0123456789ABCDEF01234567";
    private const string FamilyGuyHash = "89ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string SeveranceHash = "1111111111111111111111111111111111111111";
    private const string AndorHash = "2222222222222222222222222222222222222222";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nomercy-switch-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// Silo is switched on and saved with nothing on disk, and its download carries on. Family Guy is
    /// switched off, Severance switched on and never saved in a library with a quality, and Andor saved
    /// in a library without one:
    /// all three have nothing searched, so their downloads are cancelled and their bytes deleted — the
    /// owner's answer of 15 September 2026.
    /// </remarks>
    [Fact]
    public async Task ADownloadCarriesOnOnlyForAShowThatIsSearched()
    {
        Store database = await Opened();
        AppliedSettings applied = await Choices(database);
        HostLibrary library = new(Shelves());
        GrabRepository grabs = new(database);

        await Grabbed(grabs, new EpisodeKey(Silo, 1, 1), "Silo", SiloHash);
        await Grabbed(grabs, new EpisodeKey(FamilyGuy, 1, 1), "Family Guy", FamilyGuyHash);
        await Grabbed(grabs, new EpisodeKey(Severance, 1, 1), "Severance", SeveranceHash);
        await Grabbed(grabs, new EpisodeKey(Andor, 1, 1), "Andor", AndorHash);

        StandingEngine engine = new StandingEngine()
            .Holding(Downloading(SiloHash))
            .Holding(Downloading(FamilyGuyHash))
            .Holding(Downloading(SeveranceHash))
            .Holding(Downloading(AndorHash));

        await Tick(engine, grabs, library, applied);

        Assert.Equal(
            [SeveranceHash, AndorHash, FamilyGuyHash],
            engine.Removed.Select(removed => removed.InfoHash).Order(StringComparer.Ordinal));
        Assert.All(engine.Removed, removed => Assert.True(removed.DeleteFiles, $"{removed.InfoHash} was left on the disk."));

        StoredDownload open = Assert.Single(await grabs.OpenAsync(CancellationToken.None));

        Assert.Equal(SiloHash, open.InfoHash);
    }

    /// <remarks>
    /// An episode already staged for the encoder is past the point of cancelling: the file is in the
    /// intake folder and the encode is the library's to finish.
    /// </remarks>
    [Fact]
    public async Task AStagedEpisodeOfAShowSwitchedOffIsLeftToTheEncoder()
    {
        Store database = await Opened();
        GrabRepository grabs = new(database);

        await Grabbed(grabs, new EpisodeKey(FamilyGuy, 1, 1), "Family Guy", FamilyGuyHash);
        await grabs.StateAsync(FamilyGuyHash, GrabState.Staged, CancellationToken.None);

        StandingEngine engine = new StandingEngine().Holding(Downloading(FamilyGuyHash));

        await Tick(engine, grabs, new HostLibrary(Shelves()), await Choices(database));

        Assert.DoesNotContain(engine.Removed, removed => removed.InfoHash == FamilyGuyHash);
    }

    /// <remarks>
    /// Switched off from its row, a show has nothing searched and keeps what was saved: switched on again
    /// from the same row it is searched at once, with no form saved a second time.
    /// </remarks>
    [Fact]
    public async Task ASwitchedOffShowKeepsItsSettings()
    {
        Store database = await Opened();
        ShowSettingsRepository shows = new(database);
        MissingRefresh refresh = new(
            new HostLibrary(Shelves()),
            new AppliedSettings(shows, new LibraryPreferencesRepository(database)),
            new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero)));

        await shows.SaveAsync(new(Silo) { SwitchedOn = true, Quality = "720p" }, CancellationToken.None);

        Assert.Contains(await refresh.DeriveAsync(CancellationToken.None), episode => episode.Key.ShowId == Silo);

        await shows.SwitchAsync(Silo, on: false, CancellationToken.None);

        Assert.DoesNotContain(await refresh.DeriveAsync(CancellationToken.None), episode => episode.Key.ShowId == Silo);

        await shows.SwitchAsync(Silo, on: true, CancellationToken.None);

        Assert.Contains(await refresh.DeriveAsync(CancellationToken.None), episode => episode.Key.ShowId == Silo);
    }

    public void Dispose()
    {
        TemporaryFolder.Forget(_root);
    }

    /// <summary>
    /// Silo on and saved, Family Guy saved and off, Severance on and never saved, Andor saved with no
    /// quality anywhere. The television library has 1080p; the other has no quality.
    /// </summary>
    private static async Task<AppliedSettings> Choices(Store database)
    {
        ShowSettingsRepository shows = new(database);
        LibraryPreferencesRepository libraries = new(database);

        await libraries.SaveAsync(new(TelevisionLibrary) { Quality = "1080p" }, CancellationToken.None);

        await shows.SaveAsync(new(Silo) { SwitchedOn = true, Quality = "1080p" }, CancellationToken.None);
        await shows.SaveAsync(new(FamilyGuy) { SwitchedOn = false, Quality = "1080p" }, CancellationToken.None);
        await shows.SwitchAsync(Severance, on: true, CancellationToken.None);
        await shows.SaveAsync(new(Andor) { SwitchedOn = true }, CancellationToken.None);

        return new(shows, libraries);
    }

    /// <summary>Four shows and not one file on disk among them.</summary>
    private static FakeLibraryQuery Shelves()
    {
        return new FakeLibraryQuery()
            .Library(TelevisionLibrary, "Television", "tv")
            .Library(AnimeLibrary, "Anime", "anime")
            .Show(Silo, "Silo", TelevisionLibrary, year: 2023)
            .Episode(Silo, 1, 1, airDate: new DateTime(2023, 5, 5))
            .Show(FamilyGuy, "Family Guy", TelevisionLibrary, year: 1999)
            .Episode(FamilyGuy, 1, 1, airDate: new DateTime(2020, 1, 1))
            .Show(Severance, "Severance", TelevisionLibrary, year: 2022)
            .Episode(Severance, 1, 1, airDate: new DateTime(2022, 2, 18))
            .Show(Andor, "Andor", AnimeLibrary, year: 2022)
            .Episode(Andor, 1, 1, airDate: new DateTime(2022, 9, 21));
    }

    private async Task Tick(StandingEngine engine, GrabRepository grabs, HostLibrary library, AppliedSettings applied)
    {
        FakeProvider server = new();

        await new Transfers(
                engine,
                grabs,
                library,
                applied,
                new Stager(server.Journal, server.Log),
                EncodeGateway.For(server, server.Journal, server.Log),
                server.Journal,
                server.Log,
                TimeProvider.System)
            .TickAsync(
                Path.Combine(_root, "incomplete"),
                Path.Combine(_root, "intake"),
                CancellationToken.None);
    }

    /// <summary>Still going, so nothing else in the tick acts on it.</summary>
    private static TorrentStatus Downloading(string hash)
    {
        return new(
            hash,
            hash,
            TorrentState.Downloading,
            BytesDone: 10_000,
            BytesTotal: 900_000_000,
            DownloadRateBytesPerSecond: 1_000,
            UploadRateBytesPerSecond: 0,
            Peers: 3,
            Seeds: 2,
            Ratio: 0,
            Eta: null,
            Error: null);
    }

    private static async Task Grabbed(GrabRepository grabs, EpisodeKey key, string show, string hash)
    {
        await grabs.RecordAsync(
            key,
            show,
            $"{show}.S{key.Season:00}E{key.Number:00}.1080p.WEB.H264-CAKES",
            "1337x",
            hash,
            $"magnet:?xt=urn:btih:{hash}",
            [key],
            DateTimeOffset.UtcNow,
            CancellationToken.None);
    }

    private async Task<Store> Opened()
    {
        Store database = new(_root);

        await database.MigrateAsync(CancellationToken.None);

        return database;
    }
}

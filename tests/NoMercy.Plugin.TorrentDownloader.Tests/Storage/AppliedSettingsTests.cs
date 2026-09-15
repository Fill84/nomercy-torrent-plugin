using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Storage;

/// <summary>What a run judges each show's names against, read from the plugin's own settings.</summary>
public sealed class AppliedSettingsTests : IDisposable
{
    private const string Tv = "01HQ5W4AVF30N10RT6XCF6AJHM";

    private const string Anime = "01HQ5W4GAVF30N10RT6XCF6AJQ";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nomercy-applied-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// <c>docs/specs/show-list.md</c> § Library preferences: each show of a run is judged by its own saved
    /// settings with its own library's counted in, not by one setting for everything. Silo sets its own
    /// quality and forbids DUAL beside its library's must; Frieren follows an anime library with other
    /// preferences; Andor was never touched and follows its library.
    /// </remarks>
    [Fact]
    public async Task EveryShowOfARunGetsItsOwnSettingsWithItsLibrarysCountedIn()
    {
        Store database = new(_root);
        await database.MigrateAsync(CancellationToken.None);

        ShowSettingsRepository shows = new(database);
        LibraryPreferencesRepository libraries = new(database);

        await libraries.SaveAsync(new(Tv) { Quality = "1080p", Musts = ["WEB"] }, CancellationToken.None);
        await libraries.SaveAsync(new(Anime) { Quality = "720p", Codec = "h265", Wishes = ["DUAL"] }, CancellationToken.None);
        await shows.SaveAsync(new(41) { SwitchedOn = true, Quality = "2160p", Forbidden = ["DUAL"] }, CancellationToken.None);
        await shows.SaveAsync(new(7) { SwitchedOn = true }, CancellationToken.None);

        SettingsByShow run = await new AppliedSettings(shows, libraries).ForShowsAsync(
            [Show(41, "Silo", Tv), Show(7, "Frieren", Anime), Show(63, "Andor", Tv)],
            CancellationToken.None);

        Assert.Equal("2160p", run.For(41).Quality);
        Assert.Equal(["WEB"], run.For(41).Musts);
        Assert.Equal(["DUAL"], run.For(41).Forbidden);
        Assert.True(run.For(41).Searched);

        Assert.Equal("720p", run.For(7).Quality);
        Assert.Equal("h265", run.For(7).Codec);
        Assert.Equal(["DUAL"], run.For(7).Wishes);

        Assert.Equal("1080p", run.For(63).Quality);
        Assert.False(run.For(63).Searched);
    }

    public void Dispose()
    {
        TemporaryFolder.Forget(_root);
    }

    private static Show Show(int id, string title, string library)
    {
        return new(id, title, 2023, library, "Library", LibraryKind.Television, "/" + title);
    }
}

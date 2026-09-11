using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Storage;

/// <summary>
/// The <c>runs</c> table, against a real SQLite file.
/// </summary>
/// <remarks>
/// The server was restarted on 11 September 2026 and the dashboard said "never
/// run" for a plugin that had run a dozen times that night: when it last ran
/// was a field in memory. It is on disk now.
/// </remarks>
public class RunRepositoryTests : IAsyncLifetime
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

    /// <remarks>
    /// The last one to end, and how it ended, read back by a store opened
    /// afresh — which is what a restart is.
    /// </remarks>
    [Fact]
    public async Task TheLastRunIsStillKnownAfterARestart()
    {
        Store before = new(_folder);
        await before.MigrateAsync(CancellationToken.None);

        RunRepository runs = new(before);
        await runs.RecordAsync(new(At(3, 20), At(3, 25), RunEnd.Finished), CancellationToken.None);
        await runs.RecordAsync(new(At(5, 22), At(5, 27), RunEnd.Stopped), CancellationToken.None);

        Store after = new(_folder);
        await after.MigrateAsync(CancellationToken.None);

        Assert.Equal(
            new LastRun(At(5, 22), At(5, 27), RunEnd.Stopped),
            await new RunRepository(after).LastAsync(CancellationToken.None));
    }

    /// <remarks>
    /// Nothing is not nought: a plugin that has never run says so.
    /// </remarks>
    [Fact]
    public async Task APluginThatHasNeverRunHasNoLastRun()
    {
        Store database = new(_folder);
        await database.MigrateAsync(CancellationToken.None);

        Assert.Null(await new RunRepository(database).LastAsync(CancellationToken.None));
    }

    /// <remarks>
    /// Only the latest are kept. The page asks how the last one ended, and a
    /// table growing four times a day for ever answers nothing more for it.
    /// </remarks>
    [Fact]
    public async Task OnlyTheLatestRunsAreKept()
    {
        Store database = new(_folder);
        await database.MigrateAsync(CancellationToken.None);

        RunRepository runs = new(database);

        for (int minute = 0; minute < RunRepository.Kept + 5; minute++)
        {
            DateTimeOffset at = At(0, 0).AddMinutes(minute);
            await runs.RecordAsync(new(at, at.AddSeconds(30), RunEnd.Finished), CancellationToken.None);
        }

        await using Microsoft.Data.Sqlite.SqliteConnection connection = await database.OpenAsync(CancellationToken.None);
        await using Microsoft.Data.Sqlite.SqliteCommand count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM runs;";

        Assert.Equal((long)RunRepository.Kept, (long)(await count.ExecuteScalarAsync())!);
    }

    private static DateTimeOffset At(int hour, int minute)
    {
        return new(2026, 9, 11, hour, minute, 0, TimeSpan.Zero);
    }
}

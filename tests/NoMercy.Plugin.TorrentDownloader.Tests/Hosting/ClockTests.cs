using Microsoft.Extensions.Time.Testing;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// What decides a cadence is due, against a real SQLite file.
/// </summary>
/// <remarks>
/// The whole reason this exists: the host reads a plugin's job list only when
/// it is installed, hot-swapped or enabled, so a saved cadence has to take
/// effect some other way. It does, here — see
/// <see cref="ASavedCadenceIsDueByTheNewIntervalWithoutARestart"/>.
/// </remarks>
public class ClockTests : IAsyncLifetime
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
    /// Three cadences, three answers: one whose six-hourly slot has not come
    /// round since it last finished, one whose daily slot came round two days
    /// ago and was never asked, and one with no row at all — never run, and
    /// due at once, which is the right answer on a fresh install.
    /// </remarks>
    [Fact]
    public async Task ACadenceIsDueWhenItsIntervalHasPassedSinceItLastFinished()
    {
        Store database = new(_folder);
        await database.MigrateAsync(CancellationToken.None);

        CadenceRepository cadences = new(database);
        FakeTimeProvider time = new(new DateTimeOffset(2026, 9, 10, 4, 0, 0, TimeSpan.Zero));
        Clock clock = new(cadences, time);

        // maintenance finished two days ago; its daily 04:00 slot has come
        // round since, on the 11th and the 12th.
        await clock.FinishedAsync("maintenance", CancellationToken.None);

        // search finished five hours into the 12th; its six-hourly slot
        // (00:00, 06:00, 12:00, 18:00) has not come round since.
        time.SetUtcNow(new DateTimeOffset(2026, 9, 12, 5, 0, 0, TimeSpan.Zero));
        await clock.FinishedAsync("search", CancellationToken.None);

        // feed has never finished at all.
        time.SetUtcNow(new DateTimeOffset(2026, 9, 12, 5, 30, 0, TimeSpan.Zero));

        IReadOnlyList<string> due = await clock.DueAsync(
            new Dictionary<string, string>
            {
                ["search"] = "0 */6 * * *",
                ["maintenance"] = "0 4 * * *",
                ["feed"] = "*/15 * * * *",
            },
            time.GetUtcNow(),
            CancellationToken.None);

        Assert.DoesNotContain("search", due);
        Assert.Contains("maintenance", due);
        Assert.Contains("feed", due);
    }

    /// <remarks>
    /// This is the whole point of the slice: no restart, no re-registration —
    /// the same clock, asked again, answers differently because the owner
    /// saved a shorter interval while the server kept running.
    /// </remarks>
    [Fact]
    public async Task ASavedCadenceIsDueByTheNewIntervalWithoutARestart()
    {
        Store database = new(_folder);
        await database.MigrateAsync(CancellationToken.None);

        CadenceRepository cadences = new(database);
        FakeTimeProvider time = new(new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero));
        Clock clock = new(cadences, time);

        await clock.FinishedAsync("search", CancellationToken.None);

        time.SetUtcNow(new DateTimeOffset(2026, 9, 12, 1, 0, 0, TimeSpan.Zero));

        // At the six-hourly default, one hour on is nowhere near due.
        IReadOnlyList<string> before = await clock.DueAsync(
            new Dictionary<string, string> { ["search"] = "0 */6 * * *" },
            time.GetUtcNow(),
            CancellationToken.None);

        Assert.DoesNotContain("search", before);

        // The owner saves a much shorter cadence while the server keeps
        // running. Nothing about the last finish changes — only the
        // expression the clock is asked about.
        IReadOnlyList<string> after = await clock.DueAsync(
            new Dictionary<string, string> { ["search"] = "*/5 * * * *" },
            time.GetUtcNow(),
            CancellationToken.None);

        Assert.Contains("search", after);
    }

    /// <remarks>
    /// The settings page already refuses a bad cron on save; this is the
    /// second line of defence for a file edited by hand that never passed
    /// through it. Treating an unparseable expression as due would run that
    /// cadence every single minute from the moment somebody saved it.
    /// </remarks>
    [Fact]
    public async Task ACadenceWhoseExpressionCannotBeParsedIsNeverDue()
    {
        Store database = new(_folder);
        await database.MigrateAsync(CancellationToken.None);

        CadenceRepository cadences = new(database);
        Clock clock = new(cadences, new FakeTimeProvider());

        IReadOnlyList<string> due = await clock.DueAsync(
            new Dictionary<string, string> { ["search"] = "not a cron" },
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Empty(due);
    }
}

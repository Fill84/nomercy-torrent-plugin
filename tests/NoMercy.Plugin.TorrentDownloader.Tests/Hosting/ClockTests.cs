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
/// <see cref="ASavedCadenceTakesEffectWithoutARestart"/>.
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
    /// <para>
    /// When a cycle next falls due is counted from when the last one finished,
    /// not from the hour the server happens to have started in. A cycle that
    /// ended at five past is due again at the next slot after that.
    /// </para>
    /// <para>
    /// <strong>Nothing asks whether one is due.</strong> This used to answer
    /// "which of these are due now?" and be asked on every tick; the plugin
    /// sets one timer for the moment this returns and is woken because a cycle
    /// is due rather than to find out whether one is.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WhenTheNextCycleFallsDueIsCountedFromTheLastFinish()
    {
        Store database = new(_folder);
        await database.MigrateAsync(CancellationToken.None);

        CadenceRepository cadences = new(database);
        FakeTimeProvider time = new(new DateTimeOffset(2026, 9, 12, 5, 0, 0, TimeSpan.Zero));
        Clock clock = new(cadences, time);

        await clock.FinishedAsync("cycle", CancellationToken.None);

        // Hourly, on the hour: the one after five o'clock is six.
        Assert.Equal(
            new DateTimeOffset(2026, 9, 12, 6, 0, 0, TimeSpan.Zero),
            await clock.NextAsync("cycle", "0 * * * *", CancellationToken.None));
    }

    /// <remarks>
    /// A cycle that has never run is due at once, which is the right answer on
    /// a fresh install: the owner has just set the plugin up and is watching it.
    /// </remarks>
    [Fact]
    public async Task ACycleThatHasNeverRunIsDueAtOnce()
    {
        Store database = new(_folder);
        await database.MigrateAsync(CancellationToken.None);

        CadenceRepository cadences = new(database);
        Clock clock = new(cadences, new FakeTimeProvider(new DateTimeOffset(2026, 9, 12, 5, 30, 0, TimeSpan.Zero)));

        DateTimeOffset? next = await clock.NextAsync("cycle", "0 * * * *", CancellationToken.None);

        Assert.NotNull(next);
        Assert.True(next < DateTimeOffset.UtcNow, "a cycle that has never run was not due yet");
    }

    /// <remarks>
    /// This is the whole point of the plugin keeping its own clock: no restart,
    /// no re-registration — the same clock, asked again, answers differently
    /// because the owner saved a shorter interval while the server kept
    /// running. The host reads a plugin's job list only when it is installed,
    /// hot-swapped or enabled, and the owner changed a cadence on 3 September
    /// 2026, watched the old one go on firing, and reasonably concluded the
    /// setting did nothing.
    /// </remarks>
    [Fact]
    public async Task ASavedCadenceTakesEffectWithoutARestart()
    {
        Store database = new(_folder);
        await database.MigrateAsync(CancellationToken.None);

        CadenceRepository cadences = new(database);
        FakeTimeProvider time = new(new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero));
        Clock clock = new(cadences, time);

        await clock.FinishedAsync("cycle", CancellationToken.None);

        // Hourly, which is the default: the next one is an hour away.
        Assert.Equal(
            new DateTimeOffset(2026, 9, 12, 1, 0, 0, TimeSpan.Zero),
            await clock.NextAsync("cycle", "0 * * * *", CancellationToken.None));

        // The owner saves a shorter cadence while the server keeps running.
        // Nothing about the last finish changes — only the expression.
        Assert.Equal(
            new DateTimeOffset(2026, 9, 12, 0, 5, 0, TimeSpan.Zero),
            await clock.NextAsync("cycle", "*/5 * * * *", CancellationToken.None));
    }

    /// <remarks>
    /// The settings page already refuses a bad cron on save; this is the second
    /// line of defence for a file edited by hand that never passed through it.
    /// Null, so no timer is set at all — read as "due now", a bad expression
    /// would start a cycle every time anything looked, for ever.
    /// </remarks>
    [Fact]
    public async Task ACadenceWhoseExpressionCannotBeParsedSchedulesNothing()
    {
        Store database = new(_folder);
        await database.MigrateAsync(CancellationToken.None);

        CadenceRepository cadences = new(database);
        Clock clock = new(cadences, new FakeTimeProvider());

        Assert.Null(await clock.NextAsync("cycle", "not a cron", CancellationToken.None));
    }
}

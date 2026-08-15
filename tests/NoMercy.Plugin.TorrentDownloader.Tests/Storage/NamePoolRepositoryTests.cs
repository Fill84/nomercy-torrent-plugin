using Microsoft.Data.Sqlite;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Storage;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Storage;

/// <summary>
/// The <c>name_pool</c> table, against a real SQLite file.
/// </summary>
/// <remarks>
/// The pool is what makes a restart cheap: the names a harvest read are on disk
/// before anything reads them, so the pass that follows starts from those
/// rather than asking every feed again.
/// </remarks>
public class NamePoolRepositoryTests : IAsyncLifetime
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));

    private Database _database = null!;
    private NamePoolRepository _pool = null!;

    public async Task InitializeAsync()
    {
        _database = new(_folder);
        await _database.MigrateAsync(CancellationToken.None);
        _pool = new(_database);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }

        return Task.CompletedTask;
    }

    /// <remarks>
    /// The names outlive the cycle that found them. A harvest interrupted by a
    /// restart has already written what it read.
    /// </remarks>
    [Fact]
    public async Task NamesSurviveTheHarvestThatFoundThem()
    {
        await _pool.AddAsync(
            [
                new("silo|s03e06", "Silo.S03E06.1080p.WEB.H264-CAKES", "PreDB", When),
                new("silo|s03e06", "Silo.S03E06.720p.WEB.H264-SYLiX", "PreDB", When),
            ],
            CancellationToken.None);

        // A different repository over the same file, which is what a restart
        // amounts to.
        IReadOnlyList<(string Key, string Title, string Source)> stored = await Rows();

        Assert.Equal(2, stored.Count);
        Assert.All(stored, row => Assert.Equal("silo|s03e06", row.Key));
        Assert.All(stored, row => Assert.Equal("PreDB", row.Source));
    }

    /// <remarks>
    /// One name is one row however many feeds carried it, and however many
    /// cycles have seen it. The same scene name is on every feed that carries
    /// the show, and the pool is keyed so that it lands in one place.
    /// </remarks>
    [Fact]
    public async Task TheSameNameSeenAgainIsStillOneRow()
    {
        await _pool.AddAsync([new("silo|s03e06", "Silo.S03E06.1080p.WEB.H264-CAKES", "PreDB", When)], CancellationToken.None);
        await _pool.AddAsync(
            [new("silo|s03e06", "Silo.S03E06.1080p.WEB.H264-CAKES", "SceneSource", When.AddHours(1))],
            CancellationToken.None);

        (string Key, string Title, string Source) only = Assert.Single(await Rows());

        // The feed that carried it most recently, because that is the one that
        // still has it — and the time with it, since a name nobody has seen for
        // months is the one worth forgetting first.
        Assert.Equal("SceneSource", only.Source);
        Assert.Equal(When.AddHours(1), await SeenAt(only.Title));
    }

    /// <remarks>
    /// Read back by key, and by many keys at once: the stage that reads this
    /// has a whole cycle's worth of episodes in hand, and a query per episode
    /// is the shape of thing this plugin exists to stop doing.
    /// </remarks>
    [Fact]
    public async Task NamesAreReadBackByTheKeysTheyWereFiledUnder()
    {
        await _pool.AddAsync(
            [
                new("silo|s03e06", "Silo.S03E06.1080p.WEB.H264-CAKES", "PreDB", When),
                new("silo|s03e07", "Silo.S03E07.1080p.WEB.H264-CAKES", "PreDB", When),
                new("frierenbeyondjourneysend|s01e13", "Frieren.S01E13.1080p.WEB.x264-T3KASHi", "PreDB", When),
            ],
            CancellationToken.None);

        IReadOnlyList<PooledName> found = await _pool.ForAsync(
            ["silo|s03e06", "frierenbeyondjourneysend|s01e13"],
            CancellationToken.None);

        Assert.Equal(
            ["Frieren.S01E13.1080p.WEB.x264-T3KASHi", "Silo.S03E06.1080p.WEB.H264-CAKES"],
            found.Select(name => name.Title).Order());
    }

    /// <remarks>
    /// Asking about nothing asks the database nothing. An episode list with no
    /// misses in it is the ordinary case once the pool is warm.
    /// </remarks>
    [Fact]
    public async Task AskingForNoKeysAnswersNothing()
    {
        await _pool.AddAsync([new("silo|s03e06", "Silo.S03E06.1080p.WEB.H264-CAKES", "PreDB", When)], CancellationToken.None);

        Assert.Empty(await _pool.ForAsync([], CancellationToken.None));
    }

    /// <remarks>
    /// Nothing to write is not an error and not a write. A cycle where every
    /// feed was down leaves the pool exactly as it was.
    /// </remarks>
    [Fact]
    public async Task AddingNothingLeavesThePoolAlone()
    {
        await _pool.AddAsync([new("silo|s03e06", "Silo.S03E06.1080p.WEB.H264-CAKES", "PreDB", When)], CancellationToken.None);
        await _pool.AddAsync([], CancellationToken.None);

        Assert.Single(await Rows());
    }

    private static readonly DateTimeOffset When = new(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);

    private async Task<IReadOnlyList<(string Key, string Title, string Source)>> Rows()
    {
        await using SqliteConnection connection = await _database.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT normalised, title, source FROM name_pool ORDER BY title;";

        List<(string, string, string)> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        while (await reader.ReadAsync(CancellationToken.None))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    private async Task<DateTimeOffset> SeenAt(string title)
    {
        await using SqliteConnection connection = await _database.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT seen_at FROM name_pool WHERE title = $title;";
        command.Parameters.AddWithValue("$title", title);

        return DateTimeOffset.Parse(
            (string)(await command.ExecuteScalarAsync(CancellationToken.None))!,
            System.Globalization.CultureInfo.InvariantCulture);
    }
}

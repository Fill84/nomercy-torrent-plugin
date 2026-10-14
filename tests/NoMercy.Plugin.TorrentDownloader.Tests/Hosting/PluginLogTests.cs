using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// The plugin's own log: the level the owner chose, the file that holds what the server will not, and
/// nothing secret in either. GitHub issue #7.
/// </summary>
/// <remarks>
/// The server lets no plugin line below Information through, whatever the plugin asks for, so every Debug
/// line this plugin wrote before S14-03 went nowhere at all. The file is where they go now.
/// </remarks>
public sealed class PluginLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"), "logs");

    /// <summary>A moment late on a UTC day, so a local clock ahead of UTC would name the wrong file.</summary>
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 14, 23, 30, 0, TimeSpan.Zero));

    public void Dispose()
    {
        TemporaryFolder.Forget(Path.GetDirectoryName(_folder)!);
    }

    private string Today => Path.Combine(_folder, "torrent-downloader-2026-10-14.log");

    private string Written(string path)
    {
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    /// <remarks>
    /// Below the level is nowhere: not the file, not the server. And not formatted either — the peer and
    /// piece code says Trace and Debug many times a second, and a line nobody will read must cost nothing.
    /// </remarks>
    [Fact]
    public void ALineBelowTheChosenLevelIsWrittenNowhereAndNeverFormatted()
    {
        CapturingLogger server = new();
        PluginLog log = new(server, _folder, _time) { Level = LogLevel.Information };

        bool formatted = false;

        log.Log(LogLevel.Debug, default, "below the level", null, (said, _) =>
        {
            formatted = true;

            return said;
        });

        log.Flush();

        Assert.False(log.IsEnabled(LogLevel.Debug));
        Assert.False(log.IsEnabled(LogLevel.Trace));
        Assert.False(formatted);
        Assert.DoesNotContain("below the level", Written(Today), StringComparison.Ordinal);
        Assert.Empty(server.Lines);
    }

    /// <remarks>
    /// At or above the level goes to the day's file, named for the UTC date, under the folder the plugin was
    /// given.
    /// </remarks>
    [Fact]
    public void ALineAtOrAboveTheChosenLevelGoesToTheDaysFile()
    {
        PluginLog log = new(new CapturingLogger(), _folder, _time) { Level = LogLevel.Debug };

        log.LogDebug("chose {Release} over two others", "Silo.S02E03.1080p");
        log.LogWarning("one item failed");
        log.LogTrace("wire detail nobody asked for");
        log.Flush();

        string file = Written(Today);

        Assert.Contains("chose Silo.S02E03.1080p over two others", file, StringComparison.Ordinal);
        Assert.Contains("one item failed", file, StringComparison.Ordinal);
        Assert.DoesNotContain("wire detail", file, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The server keeps getting what it always got. A Debug line is the file's alone: the server would drop
    /// it anyway, and handing it over only costs the formatting twice.
    /// </remarks>
    [Fact]
    public void InformationAndAboveStillReachTheServerAndDebugDoesNot()
    {
        CapturingLogger server = new();
        PluginLog log = new(server, _folder, _time) { Level = LogLevel.Trace };

        log.LogInformation("grabbed {Release}", "Silo.S02E03.1080p");
        log.LogError("the plugin cannot do its job");
        log.LogDebug("the decision behind it");
        log.LogTrace("a block arrived");

        Assert.Contains(server.Entries, entry => entry.Level == LogLevel.Information && entry.Line == "grabbed Silo.S02E03.1080p");
        Assert.Contains(server.Entries, entry => entry.Level == LogLevel.Error && entry.Line == "the plugin cannot do its job");
        Assert.DoesNotContain(server.Entries, entry => entry.Level < LogLevel.Information);
    }

    /// <remarks>
    /// One file a UTC day and the newest seven kept — and only the plugin's own files go: something else in
    /// the folder is not the plugin's to delete, whatever it is called.
    /// </remarks>
    [Fact]
    public void EachUtcDayHasItsOwnFileAndTheNewestSevenAreKept()
    {
        Directory.CreateDirectory(_folder);

        string notOurs = Path.Combine(_folder, "notes.txt");
        string lookalike = Path.Combine(_folder, "torrent-downloader-backup.log");

        File.WriteAllText(notOurs, "the owner's");
        File.WriteAllText(lookalike, "the owner's too");

        PluginLog log = new(new CapturingLogger(), _folder, _time) { Level = LogLevel.Information };

        for (int day = 0; day < 9; day++)
        {
            log.LogInformation("day {Day}", day);
            log.Flush();
            _time.Advance(TimeSpan.FromDays(1));
        }

        string[] kept = [.. Directory.GetFiles(_folder, "torrent-downloader-????-??-??.log").Select(Path.GetFileName).Order()!];

        Assert.Equal(
            [
                "torrent-downloader-2026-10-16.log",
                "torrent-downloader-2026-10-17.log",
                "torrent-downloader-2026-10-18.log",
                "torrent-downloader-2026-10-19.log",
                "torrent-downloader-2026-10-20.log",
                "torrent-downloader-2026-10-21.log",
                "torrent-downloader-2026-10-22.log",
            ],
            kept);

        Assert.Contains("day 8", Written(Path.Combine(_folder, "torrent-downloader-2026-10-22.log")), StringComparison.Ordinal);
        Assert.DoesNotContain("day 7", Written(Path.Combine(_folder, "torrent-downloader-2026-10-22.log")), StringComparison.Ordinal);
        Assert.True(File.Exists(notOurs));
        Assert.True(File.Exists(lookalike));
    }

    /// <remarks>
    /// <para>
    /// A secret the plugin holds is blanked wherever it turns up in a line, and a secret-named parameter is
    /// blanked whatever its value — the rule <c>Addresses.Redact</c> keeps for a failure's address, kept here
    /// for every line, because a Debug line about a tracker is exactly where an announce address is said.
    /// </para>
    /// <para>
    /// The passkey in the path is the case the parameter rule cannot see: a private tracker's announce
    /// address often carries it as a path segment, and only knowing the value finds it there.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APasskeyOrApiKeyHandedToALineNeverReachesTheFileOrTheServer()
    {
        const string passkey = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
        const string apiKey = "nm-indexer-key-4417-zzz";
        const string unheld = "never-stored-but-named-9931";

        FakeSecretStore secrets = new();

        await secrets.SetAsync("tracker:some-tracker:passkey", passkey);
        await secrets.SetAsync("indexer:some-indexer:apikey", apiKey);

        CapturingLogger server = new();
        PluginLog log = new(server, _folder, _time) { Level = LogLevel.Trace };

        await log.LearnSecretsAsync(secrets, CancellationToken.None);

        log.LogDebug("Announcing to {Tracker}", $"https://tracker.example/{passkey}/announce");
        log.LogInformation("Searching {Address}", $"https://indexer.example/api?apikey={apiKey}&t=search");
        log.LogWarning("Fetching {Address} failed", $"https://feed.example/rss?passkey={unheld}");
        log.LogError(new InvalidOperationException($"refused https://tracker.example/{passkey}/announce"), "It failed");
        log.Flush();

        string everything = string.Join(" ", [Written(Today), .. server.Lines]);

        foreach (string secret in new[] { passkey, apiKey, unheld })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.OrdinalIgnoreCase);
        }

        // And it still says where, or the line has lost the only thing it was for.
        Assert.Contains("tracker.example", Written(Today), StringComparison.Ordinal);
        Assert.Contains("indexer.example", Written(Today), StringComparison.Ordinal);
        Assert.Contains("passkey=***", Written(Today), StringComparison.Ordinal);
    }

    /// <remarks>
    /// A full disk or a locked file costs lines of a log and never a download: the caller of a log line is a
    /// peer conversation or a staging, and an exception out of the log would end it. Here the folder cannot
    /// even be made, because a file stands where it would be.
    /// </remarks>
    [Fact]
    public void AFileThatCannotBeWrittenNeverReachesTheCaller()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_folder)!);
        File.WriteAllText(_folder, "a file where the folder would be");

        CapturingLogger server = new();
        PluginLog log = new(server, _folder, _time) { Level = LogLevel.Trace };

        Exception? thrown = Record.Exception(() =>
        {
            log.LogDebug("nowhere to go");
            log.LogWarning("still said to the server");
            log.Flush();
        });

        Assert.Null(thrown);
        Assert.Contains("still said to the server", server.Lines);
    }

    /// <remarks>
    /// Many threads say things at once — every peer conversation is its own — and each line arrives whole and
    /// once.
    /// </remarks>
    [Fact]
    public async Task ManyThreadsAtOnceLoseNoLineAndTearNone()
    {
        PluginLog log = new(new CapturingLogger(), _folder, _time) { Level = LogLevel.Trace };

        await Task.WhenAll(Enumerable.Range(0, 8).Select(thread => Task.Run(() =>
        {
            for (int line = 0; line < 500; line++)
            {
                log.LogTrace("thread {Thread} line {Line} end", thread, line);
            }
        })));

        log.Flush();

        string[] lines = File.ReadAllLines(Today);

        Assert.Equal(4000, lines.Length);
        Assert.All(lines, line => Assert.EndsWith(" end", line, StringComparison.Ordinal));
        Assert.Equal(4000, lines.Distinct(StringComparer.Ordinal).Count());
    }
}

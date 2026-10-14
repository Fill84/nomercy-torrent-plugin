using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// Each line at the level that matches it. GitHub issue #7: Information is one line per step the owner sees
/// — search, grab, complete, move — and Debug is the decision behind each.
/// </summary>
public sealed class EveryLineAtItsLevelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        TemporaryFolder.Forget(_folder);
    }

    /// <remarks>
    /// A search says how it went in one line, and each grab in one line of its own, at Information; why an
    /// episode was not grabbed is the decision behind the step, at Debug. Until now a search said that it
    /// started and nothing else: what it grabbed was on the Activity page and in no log at all.
    /// </remarks>
    [Fact]
    public void ASearchSaysWhatItGrabbedAtInformationAndWhyNotAtDebug()
    {
        CapturingLogger log = new();

        SearchSummary.Tell(
            log,
            new CycleReport(
            [
                new(new EpisodeKey(41, 3, 6), "Silo.S03E06.1080p.WEB.H264-CAKES", "eztv", 120, HandedOver: true, "taken")
                {
                    Searched = true,
                },
                new(new EpisodeKey(41, 3, 7), null, null, null, HandedOver: false, "no release passed the show's settings")
                {
                    Searched = true,
                },
            ]));

        Assert.Contains(
            log.Entries,
            entry => entry.Level == LogLevel.Information
                     && entry.Line.Contains("Silo.S03E06.1080p.WEB.H264-CAKES", StringComparison.Ordinal)
                     && entry.Line.Contains("eztv", StringComparison.Ordinal));
        Assert.Contains(
            log.Entries,
            entry => entry.Level == LogLevel.Information
                     && entry.Line.Contains("2 episodes", StringComparison.Ordinal)
                     && entry.Line.Contains("grabbed 1", StringComparison.Ordinal));
        Assert.Contains(
            log.Entries,
            entry => entry.Level == LogLevel.Debug
                     && entry.Line.Contains("S03E07", StringComparison.Ordinal)
                     && entry.Line.Contains("no release passed the show's settings", StringComparison.Ordinal));
        Assert.DoesNotContain(
            log.Entries,
            entry => entry.Level >= LogLevel.Information && entry.Line.Contains("S03E07", StringComparison.Ordinal));
    }

    /// <remarks>
    /// The torrent client's lines reach the plugin's own file at the owner's level, and below it the client
    /// is told nobody is listening, so it builds no line at all.
    /// </remarks>
    [Fact]
    public void TheClientsLinesReachThePluginsFileAtTheOwnersLevel()
    {
        PluginLog log = new(new CapturingLogger(), _folder) { Level = LogLevel.Information };
        ClientLog client = new(log);

        Assert.False(client.Debugging);
        Assert.False(client.Tracing);

        log.Level = LogLevel.Trace;

        Assert.True(client.Debugging);
        Assert.True(client.Tracing);

        client.Debug("announced to http://two.example: 50 peers");
        client.Trace("a block of piece 7 arrived");
        log.Flush();

        string file = string.Join("\n", Directory.GetFiles(_folder).Select(File.ReadAllText));

        Assert.Contains("[Debug] announced to http://two.example: 50 peers", file, StringComparison.Ordinal);
        Assert.Contains("[Trace] a block of piece 7 arrived", file, StringComparison.Ordinal);
    }
}

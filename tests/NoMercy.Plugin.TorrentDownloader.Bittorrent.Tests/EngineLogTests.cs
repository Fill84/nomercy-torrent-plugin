using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

public class EngineLogTests
{
    /// <remarks>
    /// A tracker is named by its scheme, host and port and nothing else. The user part of an address is
    /// where a tracker that takes a login keeps it — <c>http://user:secret@host/announce</c> — and it is as
    /// much the owner's secret as a passkey in the path.
    /// </remarks>
    [Theory]
    [InlineData("http://member:s3cr3t-word@tracker.example:2710/a1b2c3d4e5f60718293a4b5c6d7e8f90/announce", "http://tracker.example:2710")]
    [InlineData("https://member:s3cr3t-word@tracker.example/announce?passkey=s3cr3t-word", "https://tracker.example")]
    [InlineData("udp://tracker.example:6969/announce", "udp://tracker.example:6969")]
    public void ATrackerIsNamedByItsHostAloneAndNeverByItsLogin(string address, string named)
    {
        Assert.Equal(named, EngineLog.Host(address));
        Assert.DoesNotContain("s3cr3t", EngineLog.Host(address), StringComparison.Ordinal);
    }
}

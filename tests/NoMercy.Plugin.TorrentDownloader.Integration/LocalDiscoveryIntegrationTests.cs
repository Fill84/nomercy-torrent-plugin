using NoMercy.Plugin.TorrentDownloader.Bittorrent;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Integration;

/// <summary>
/// Local discovery on this machine's own network.
/// </summary>
/// <remarks>
/// It is here rather than in the ordinary suite because it is a real socket on
/// a real multicast group: a machine with no multicast route, or a network that
/// filters the group, fails it — and that is a fact about the network rather
/// than about the code. The message itself is tested without a socket in
/// <c>PeerExchangeTests</c>.
/// </remarks>
public class LocalDiscoveryIntegrationTests
{
    /// <remarks>
    /// One client announces and another hears it, on the group, with the
    /// address taken from where the packet came from rather than from anything
    /// inside it.
    /// </remarks>
    [Fact]
    public async Task AnAnnounceOnTheMulticastGroupIsHeardByAnotherClientIntegration()
    {
        using LsdSocket listening = new();
        using LsdSocket announcing = new();

        using CancellationTokenSource waiting = new(TimeSpan.FromSeconds(10));

        Task<(LsdAnnounce Announce, System.Net.IPAddress From)> heard =
            listening.ReceiveAsync(ours: "listener-cookie", waiting.Token);

        // Twice, because the first packet on a freshly joined group is lost
        // often enough on Windows to make a test that sends one a coin toss.
        for (int attempt = 0; attempt < 2 && !heard.IsCompleted; attempt++)
        {
            await announcing.AnnounceAsync(51413, [Ubuntu], "announcer-cookie", waiting.Token);

            await Task.WhenAny(heard, Task.Delay(TimeSpan.FromSeconds(2), waiting.Token));
        }

        (LsdAnnounce announce, System.Net.IPAddress from) = await heard;

        Assert.Equal(51413, announce.Port);
        Assert.Equal([Ubuntu], announce.InfoHashes);
        Assert.Equal("announcer-cookie", announce.Cookie);
        Assert.NotNull(from);
    }

    /// <remarks>
    /// Every packet comes back round the group to the client that sent it. One
    /// that took its own announce would spend the afternoon connecting to
    /// itself, and this is the socket half of that — the cookie is the only
    /// thing that tells them apart, since the address is this machine either
    /// way.
    /// </remarks>
    [Fact]
    public async Task AClientDoesNotHearItsOwnAnnounceIntegration()
    {
        using LsdSocket socket = new();

        using CancellationTokenSource waiting = new(TimeSpan.FromSeconds(4));

        Task<(LsdAnnounce Announce, System.Net.IPAddress From)> heard =
            socket.ReceiveAsync(ours: "same-cookie", waiting.Token);

        await socket.AnnounceAsync(51413, [Ubuntu], "same-cookie", waiting.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => heard);
    }

    /// <remarks>
    /// One hop. The packet is meant for this network and no further, and a
    /// router that forwarded it would be handing a list of what is being
    /// downloaded to whatever is on the other side.
    /// </remarks>
    [Fact]
    public void AnAnnounceNeverLeavesThisNetworkIntegration()
    {
        using LsdSocket socket = new();

        Assert.Equal(
            1,
            socket.MulticastTimeToLive);
    }

    private const string Ubuntu = "D160B8D8EA35A5B4E52837468FC8F03D55CEF1F7";
}

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
    /// <para>
    /// Every packet comes back round the group to the client that sent it. One
    /// that took its own announce would spend the afternoon connecting to
    /// itself, and this is the socket half of that — the cookie is the only
    /// thing that tells them apart, since the address is this machine either
    /// way.
    /// </para>
    /// <para>
    /// <strong>It used to assert that nothing at all was heard, and that is not
    /// the rule.</strong> The group belongs to the whole machine and anybody on
    /// it may announce: a second copy of this client in another test project
    /// running beside this one is heard here, and the test failed for it — on
    /// 13 September 2026, about half the time, while nothing at all was wrong.
    /// A test that fails for something it is not about is worse than no test,
    /// because the next person reads a real failure as that one.
    /// </para>
    /// <para>
    /// So it announces something nobody else can be announcing, and asserts
    /// what it is really about: whatever comes back round the group in the
    /// window, this client's own packet is never among it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AClientDoesNotHearItsOwnAnnounceIntegration()
    {
        // Unique to this run, so no neighbour on the group can be mistaken for
        // it and it cannot be mistaken for a neighbour.
        string mine = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20));
        string cookie = "same-cookie-" + mine[..8];

        using LsdSocket socket = new();

        using CancellationTokenSource waiting = new(TimeSpan.FromSeconds(4));

        await socket.AnnounceAsync(51413, [mine], cookie, waiting.Token);

        while (!waiting.IsCancellationRequested)
        {
            try
            {
                (LsdAnnounce announce, System.Net.IPAddress _) =
                    await socket.ReceiveAsync(ours: cookie, waiting.Token);

                Assert.DoesNotContain(mine, announce.InfoHashes);
            }
            catch (OperationCanceledException)
            {
                // The window closed with nothing of this client's heard, which
                // is the rule holding.
                break;
            }
        }
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

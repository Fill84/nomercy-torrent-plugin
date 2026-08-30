using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// What one peer was asked for, and what it may therefore send.
/// </summary>
/// <remarks>
/// A block nobody asked for is not a gift: it is memory this process did not
/// plan to spend, written at an offset nothing is expecting, and the peer
/// sending it is either broken or trying something.
/// </remarks>
public class RequestLedgerTests
{
    [Fact]
    public void ABlockNobodyAskedForIsRefused()
    {
        RequestLedger ledger = new();

        Assert.False(ledger.Accept(0, 0, 16384));
    }

    [Fact]
    public void ABlockThisPeerWasAskedForIsTakenOnceAndOnceOnly()
    {
        RequestLedger ledger = new();

        ledger.Asked(3, 16384, 16384);

        Assert.Equal(1, ledger.InFlight);
        Assert.True(ledger.Accept(3, 16384, 16384));

        // And not a second time. A peer answering one request twice is sending
        // a block nobody has outstanding, which is the case above by another
        // route.
        Assert.False(ledger.Accept(3, 16384, 16384));
        Assert.Equal(0, ledger.InFlight);
    }

    /// <remarks>
    /// The endgame asks several peers for the same block and cancels the rest
    /// when one answers. A cancelled request is no longer outstanding, so what
    /// arrives against it afterwards is unasked-for like any other.
    /// </remarks>
    [Fact]
    public void ACancelledRequestIsNoLongerOutstanding()
    {
        RequestLedger ledger = new();

        ledger.Asked(7, 0, 16384);
        ledger.Cancelled(7, 0, 16384);

        Assert.Equal(0, ledger.InFlight);
        Assert.False(ledger.Accept(7, 0, 16384));
    }

    /// <remarks>
    /// A block of the right piece at the wrong offset, or the right offset at
    /// the wrong length, is not the block that was asked for.
    /// </remarks>
    [Fact]
    public void ABlockThatDoesNotMatchWhatWasAskedForIsRefused()
    {
        RequestLedger ledger = new();

        ledger.Asked(2, 0, 16384);

        Assert.False(ledger.Accept(2, 16384, 16384));
        Assert.False(ledger.Accept(2, 0, 32768));
        Assert.False(ledger.Accept(5, 0, 16384));

        // And the real one still is.
        Assert.True(ledger.Accept(2, 0, 16384));
    }
}

using System.Collections.Concurrent;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// One metadata fetch, fed by every peer conversation at once.
/// </summary>
/// <remarks>
/// A magnet dials many peers and each conversation runs on its own. Every one
/// of them that has the metadata hands its pieces to the same fetch, so the
/// fetch is written from several threads at the same moment — which is how it
/// is used in production, and what a test feeding it from one thread never
/// sees. The metadata is the real info dictionary out of
/// <c>tests/fixtures/ubuntu-desktop.torrent</c>, checked against Ubuntu's own
/// published info hash.
/// </remarks>
public class MetadataFetchConcurrencyTests
{
    /// <remarks>
    /// <para>
    /// Each piece arrives from a different peer, all released together. A
    /// fetch that kept its bookkeeping in an unguarded set lost pieces when two
    /// of them grew that set at the same moment: every byte had arrived, and
    /// the fetch still said it was waiting for a piece nobody would send again,
    /// so the magnet sat on "fetching metadata" until it timed out.
    /// </para>
    /// <para>
    /// Repeated, because a race is a matter of odds. Each round is a fresh
    /// fetch, so one lucky round cannot hide an unlucky one.
    /// </para>
    /// </remarks>
    [Fact]
    public void PiecesArrivingFromManyPeersAtOnceAreAllKept()
    {
        byte[] info = UbuntuInfo();

        for (int round = 0; round < 300; round++)
        {
            MetadataFetch fetch = new(Ubuntu, info.Length);
            int pieces = fetch.Pieces;
            using Barrier start = new(pieces);

            Thread[] peers = new Thread[pieces];

            // Collected rather than left to escape: an exception thrown on a
            // thread of its own takes the whole test run down with it, and
            // says nothing about which round it was.
            ConcurrentQueue<Exception> faults = new();

            for (int piece = 0; piece < pieces; piece++)
            {
                int mine = piece;

                peers[piece] = new Thread(() =>
                {
                    start.SignalAndWait();

                    try
                    {
                        fetch.Add(mine, Slice(info, mine), $"203.0.113.{mine}:51413");
                    }
                    catch (Exception fault)
                    {
                        faults.Enqueue(fault);
                    }
                });

                peers[piece].Start();
            }

            foreach (Thread peer in peers)
            {
                // A set corrupted badly enough can spin for ever. That is a
                // failure too, and it has to be reported as one rather than as
                // a test run that never ends.
                Assert.True(peer.Join(TimeSpan.FromSeconds(10)), $"round {round}: a peer never finished adding its piece");
            }

            Assert.True(faults.IsEmpty, $"round {round}: adding a piece threw {faults.FirstOrDefault()}");
            Assert.True(fetch.Complete, $"round {round}: every piece was added and the fetch is not complete");
            Assert.Empty(fetch.Wanted());
            Assert.True(fetch.Verified, $"round {round}: the metadata does not hash to Ubuntu's info hash");
            Assert.Equal(pieces, fetch.Contributors.Count);
        }
    }

    /// <summary>Ubuntu's published info hash for the fixture torrent.</summary>
    private static byte[] Ubuntu =>
        Convert.FromHexString("D160B8D8EA35A5B4E52837468FC8F03D55CEF1F7");

    /// <summary>The raw info dictionary out of the real torrent.</summary>
    private static byte[] UbuntuInfo()
    {
        byte[] torrent = Fixture("ubuntu-desktop.torrent");
        BencodeDocument document = Bencode.Read(torrent);

        return torrent[document.InfoStart!.Value..(document.InfoStart.Value + document.InfoLength!.Value)];
    }

    /// <summary>One sixteen-kibibyte piece of the metadata, the last one short.</summary>
    private static byte[] Slice(byte[] info, int piece)
    {
        int at = piece * MetadataTransfer.PieceLength;

        return info[at..Math.Min(at + MetadataTransfer.PieceLength, info.Length)];
    }

    private static byte[] Fixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllBytes(Path.Combine(directory!.FullName, "tests", "fixtures", name));
    }
}

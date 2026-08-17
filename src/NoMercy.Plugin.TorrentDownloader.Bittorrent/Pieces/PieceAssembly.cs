using System.Security.Cryptography;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>What became of a piece when its last block arrived.</summary>
public enum PieceOutcome
{
    /// <summary>Still missing blocks.</summary>
    Incomplete,

    /// <summary>Complete and its SHA-1 is the one the torrent named.</summary>
    Verified,

    /// <summary>
    /// Complete and wrong. Discarded whole: there is no way to tell which block
    /// was the bad one, and keeping any of it would put the fault on disk.
    /// </summary>
    Failed,
}

/// <summary>
/// One piece being put together out of blocks from several peers.
/// </summary>
/// <remarks>
/// It remembers who contributed. A piece that fails its hash was ruined by one
/// of them, and there is no way to say which — so every contributor is
/// penalised, and a peer that has been in two failures is not worth the
/// bandwidth.
/// </remarks>
public sealed class PieceAssembly(int piece, long length, byte[] expectedHash)
{
    private readonly byte[] _bytes = new byte[length];
    private readonly HashSet<int> _have = [];
    private readonly HashSet<string> _contributors = [];

    /// <summary>Which piece.</summary>
    public int Piece => piece;

    /// <summary>Everybody who sent part of it.</summary>
    public IReadOnlyCollection<string> Contributors => _contributors;

    /// <summary>Whether every block has arrived.</summary>
    public bool Complete => _have.Count == BlockCount;

    /// <summary>How many blocks it is made of.</summary>
    public int BlockCount => (int)((length + PeerMessage.BlockLength - 1) / PeerMessage.BlockLength);

    /// <summary>The bytes, once it is complete and verified.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>
    /// Takes one block from one peer.
    /// </summary>
    /// <remarks>
    /// The offset has to be a block boundary and the length has to be what that
    /// block is. A peer sending overlapping blocks could otherwise rewrite what
    /// another peer already contributed, and the piece would fail its hash with
    /// nothing to show which peer did it.
    /// </remarks>
    public PieceOutcome Add(int offset, ReadOnlySpan<byte> data, string peer)
    {
        if (offset < 0 || offset % PeerMessage.BlockLength != 0 || offset + data.Length > length)
        {
            throw new PeerProtocolException(
                $"A block at {offset} of {data.Length} bytes is not part of a piece of {length}.");
        }

        data.CopyTo(_bytes.AsSpan(offset));
        _have.Add(offset / PeerMessage.BlockLength);
        _contributors.Add(peer);

        if (!Complete)
        {
            return PieceOutcome.Incomplete;
        }

        // The whole point of the piece: twenty bytes the torrent named, over
        // the bytes that arrived. Nothing is written to disk before this.
        return SHA1.HashData(_bytes).AsSpan().SequenceEqual(expectedHash)
            ? PieceOutcome.Verified
            : PieceOutcome.Failed;
    }
}

/// <summary>
/// How much each peer has been trusted with, and who is no longer welcome.
/// </summary>
/// <remarks>
/// Two failed pieces bans a peer for the session, from
/// docs/06-torrent-client.md. One failure can be somebody else's fault — a
/// piece has several contributors and only one of them ruined it — but a peer
/// present at two is the one they had in common.
/// </remarks>
public sealed class PeerTrust
{
    private readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);

    /// <summary>How many failed pieces this peer contributed to.</summary>
    public const int Forgiven = 1;

    /// <summary>Notes a piece that failed, against everybody who sent part of it.</summary>
    public void Failed(IEnumerable<string> contributors)
    {
        foreach (string peer in contributors)
        {
            _failures[peer] = _failures.GetValueOrDefault(peer) + 1;
        }
    }

    /// <summary>How many failures this peer has been part of.</summary>
    public int Failures(string peer)
    {
        return _failures.GetValueOrDefault(peer);
    }

    /// <summary>Whether this peer is done for the session.</summary>
    public bool Banned(string peer)
    {
        return Failures(peer) > Forgiven;
    }
}

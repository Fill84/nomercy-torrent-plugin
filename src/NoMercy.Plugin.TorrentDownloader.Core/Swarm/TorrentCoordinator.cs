// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Peers;
using NoMercy.Plugin.TorrentDownloader.Core.Pieces;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;

namespace NoMercy.Plugin.TorrentDownloader.Core.Swarm;

/// <summary>
/// The single owner of everything mutable about one torrent.
///
/// <para>
/// Peers own nothing. They report what they have and what arrived; this decides what
/// to ask for next. There are no locks here because there is one writer - the design
/// decision the whole engine rests on, and the reason a hundred peers do not contend.
/// </para>
///
/// <para>
/// It performs nothing. Every method returns the actions it decided on, so the same
/// logic that runs against a hundred sockets can be driven by a test posting messages.
/// </para>
/// </summary>
public sealed class TorrentCoordinator(TorrentMetadata metadata, Bitfield have, SwarmPolicy policy)
{
    /// <summary>16 KiB, which is what every client asks for and what most refuse to exceed.</summary>
    public const int BlockLength = 16 * 1024;

    /// <summary>Requests kept outstanding per peer. One at a time would idle for a full round trip between blocks.</summary>
    private const int PipelineDepth = 5;

    private readonly Dictionary<PeerKey, PeerState> _peers = [];
    private readonly Dictionary<int, PartialPiece> _partials = [];
    private readonly int[] _availability = new int[metadata.PieceCount];

    /// <summary>Which peers we are currently waiting on for a block. More than one only in endgame.</summary>
    private readonly Dictionary<(int Piece, int Begin), HashSet<PeerKey>> _inFlight = [];

    public Bitfield Have { get; } = have;

    public bool IsComplete => Have.IsComplete;

    public IReadOnlyList<CoordinatorAction> PeerAnnouncedBitfield(PeerKey peer, Bitfield theirs)
    {
        PeerState state = Track(peer);

        for (int index = 0; index < metadata.PieceCount; index++)
        {
            if (theirs[index] && !state.Has[index])
            {
                state.Has[index] = true;
                _availability[index]++;
            }
        }

        List<CoordinatorAction> actions = [];
        AnnounceInterest(peer, state, actions);
        return actions;
    }

    public IReadOnlyList<CoordinatorAction> PeerAnnouncedHave(PeerKey peer, int pieceIndex)
    {
        PeerState state = Track(peer);
        List<CoordinatorAction> actions = [];

        if (pieceIndex < 0 || pieceIndex >= metadata.PieceCount || state.Has[pieceIndex])
            return actions;

        state.Has[pieceIndex] = true;
        _availability[pieceIndex]++;

        AnnounceInterest(peer, state, actions);
        RequestMore(peer, state, actions);

        return actions;
    }

    public IReadOnlyList<CoordinatorAction> PeerUnchoked(PeerKey peer)
    {
        PeerState state = Track(peer);
        state.Choking = false;

        List<CoordinatorAction> actions = [];
        RequestMore(peer, state, actions);
        return actions;
    }

    public IReadOnlyList<CoordinatorAction> PeerChoked(PeerKey peer)
    {
        PeerState state = Track(peer);
        state.Choking = true;

        // Their outstanding requests will never be answered, so release the blocks
        // rather than leaving them reserved against a peer that has stopped talking.
        ReleaseOutstanding(peer, state);

        return [];
    }

    public IReadOnlyList<CoordinatorAction> PeerDisconnected(PeerKey peer)
    {
        if (!_peers.TryGetValue(peer, out PeerState? state))
            return [];

        ReleaseOutstanding(peer, state);

        for (int index = 0; index < metadata.PieceCount; index++)
        {
            if (state.Has[index])
                _availability[index]--;
        }

        _peers.Remove(peer);

        return [];
    }

    public IReadOnlyList<CoordinatorAction> BlockReceived(PeerKey peer, int pieceIndex, int begin, byte[] block)
    {
        List<CoordinatorAction> actions = [];

        if (!_peers.TryGetValue(peer, out PeerState? state))
            return actions;

        // A block nobody asked this peer for is not evidence of anything. Accepting it
        // would let any peer write into a piece it was never trusted with.
        if (!state.Outstanding.Remove((pieceIndex, begin)))
            return actions;

        ReleaseBlock(peer, pieceIndex, begin);

        if (Have[pieceIndex])
        {
            RequestMore(peer, state, actions);
            return actions;
        }

        PartialPiece partial = Partial(pieceIndex);

        if (!partial.Accept(begin, block, peer))
        {
            RequestMore(peer, state, actions);
            return actions;
        }

        if (partial.IsComplete)
            CompletePiece(pieceIndex, partial, actions);

        RequestMore(peer, state, actions);

        return actions;
    }

    private void CompletePiece(int pieceIndex, PartialPiece partial, List<CoordinatorAction> actions)
    {
        _partials.Remove(pieceIndex);

        if (!PieceVerifier.Matches(metadata, pieceIndex, partial.Buffer))
        {
            actions.Add(new PieceRejected(pieceIndex));

            // A piece is assembled from blocks by several peers, so a bad hash does not
            // name a culprit. Debit everyone who contributed: one failure is luck, a
            // peer that keeps appearing in failed pieces is the pattern.
            foreach (PeerKey contributor in partial.Contributors)
            {
                if (!_peers.TryGetValue(contributor, out PeerState? guilty))
                    continue;

                guilty.PieceFailures++;

                if (policy.ShouldBan(guilty.PieceFailures))
                    actions.Add(new BanPeer(contributor));
            }

            return;
        }

        Have[pieceIndex] = true;
        actions.Add(new PieceReady(pieceIndex, partial.Buffer));

        foreach (PeerKey other in _peers.Keys)
            actions.Add(new SendMessage(other, new Have(pieceIndex)));

        // In endgame the same block may be outstanding with several peers. Now that it
        // is in, tell the others to stop rather than paying for it twice.
        foreach ((PeerKey key, PeerState peerState) in _peers)
        {
            foreach ((int piece, int offset) in peerState.Outstanding.Where(block => block.Piece == pieceIndex).ToList())
            {
                peerState.Outstanding.Remove((piece, offset));
                ReleaseBlock(key, piece, offset);
                actions.Add(new SendMessage(key, new Cancel(piece, offset, LengthOfBlock(piece, offset))));
            }
        }
    }

    private void AnnounceInterest(PeerKey peer, PeerState state, List<CoordinatorAction> actions)
    {
        if (state.Interested || !HasAnythingWeNeed(state))
            return;

        state.Interested = true;
        actions.Add(new SendMessage(peer, new Interested()));
    }

    private bool HasAnythingWeNeed(PeerState state)
    {
        for (int index = 0; index < metadata.PieceCount; index++)
        {
            if (state.Has[index] && !Have[index])
                return true;
        }

        return false;
    }

    private void RequestMore(PeerKey peer, PeerState state, List<CoordinatorAction> actions)
    {
        if (state.Choking || IsComplete)
            return;

        bool endgame = policy.ShouldEnterEndgame(Have.Length - Have.SetCount, Have.Length);

        while (state.Outstanding.Count < PipelineDepth)
        {
            (int Piece, int Begin)? next = NextBlockFor(peer, state, endgame);

            if (next is not (int piece, int begin))
                return;

            state.Outstanding.Add((piece, begin));
            Reserve(peer, piece, begin);

            actions.Add(new SendMessage(peer, new Request(piece, begin, LengthOfBlock(piece, begin))));
        }
    }

    /// <summary>
    /// Rarest first. A piece held by one peer is fetched while that peer is still here;
    /// a piece everybody has will still be available later.
    /// </summary>
    private (int Piece, int Begin)? NextBlockFor(PeerKey peer, PeerState state, bool endgame)
    {
        IEnumerable<int> candidates = Enumerable.Range(0, metadata.PieceCount)
            .Where(index => !Have[index] && state.Has[index])
            .OrderBy(index => _availability[index])
            .ThenBy(index => index);

        foreach (int piece in candidates)
        {
            int length = metadata.LengthOfPiece(piece);

            for (int begin = 0; begin < length; begin += BlockLength)
            {
                if (Partial(piece).HasBlock(begin))
                    continue;

                bool reserved = _inFlight.TryGetValue((piece, begin), out HashSet<PeerKey>? holders);

                if (reserved && (!endgame || holders!.Contains(peer)))
                    continue;

                return (piece, begin);
            }
        }

        return null;
    }

    private void Reserve(PeerKey peer, int piece, int begin)
    {
        if (!_inFlight.TryGetValue((piece, begin), out HashSet<PeerKey>? holders))
            _inFlight[(piece, begin)] = holders = [];

        holders.Add(peer);
    }

    private void ReleaseBlock(PeerKey peer, int piece, int begin)
    {
        if (!_inFlight.TryGetValue((piece, begin), out HashSet<PeerKey>? holders))
            return;

        holders.Remove(peer);

        if (holders.Count == 0)
            _inFlight.Remove((piece, begin));
    }

    private void ReleaseOutstanding(PeerKey peer, PeerState state)
    {
        foreach ((int piece, int begin) in state.Outstanding)
            ReleaseBlock(peer, piece, begin);

        state.Outstanding.Clear();
    }

    private int LengthOfBlock(int piece, int begin) =>
        Math.Min(BlockLength, metadata.LengthOfPiece(piece) - begin);

    private PeerState Track(PeerKey peer)
    {
        if (!_peers.TryGetValue(peer, out PeerState? state))
            _peers[peer] = state = new PeerState(metadata.PieceCount);

        return state;
    }

    private PartialPiece Partial(int pieceIndex)
    {
        if (!_partials.TryGetValue(pieceIndex, out PartialPiece? partial))
            _partials[pieceIndex] = partial = new PartialPiece(metadata.LengthOfPiece(pieceIndex));

        return partial;
    }

    private sealed class PeerState(int pieceCount)
    {
        public Bitfield Has { get; } = new(pieceCount);

        /// <summary>Peers start choking. Nothing may be requested until they say otherwise.</summary>
        public bool Choking { get; set; } = true;

        public bool Interested { get; set; }

        public int PieceFailures { get; set; }

        public HashSet<(int Piece, int Begin)> Outstanding { get; } = [];
    }

    private sealed class PartialPiece(int length)
    {
        private readonly bool[] _received = new bool[(length + BlockLength - 1) / BlockLength];

        public byte[] Buffer { get; } = new byte[length];

        public HashSet<PeerKey> Contributors { get; } = [];

        public bool IsComplete => _received.All(received => received);

        public bool HasBlock(int begin) => _received[begin / BlockLength];

        public bool Accept(int begin, byte[] block, PeerKey from)
        {
            if (begin < 0 || begin >= Buffer.Length || begin % BlockLength != 0)
                return false;

            int expected = Math.Min(BlockLength, Buffer.Length - begin);

            if (block.Length != expected || _received[begin / BlockLength])
                return false;

            block.CopyTo(Buffer, begin);
            _received[begin / BlockLength] = true;
            Contributors.Add(from);

            return true;
        }
    }
}

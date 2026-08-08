// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Threading.Channels;
using NoMercy.Plugin.TorrentDownloader.Core.Peers;
using NoMercy.Plugin.TorrentDownloader.Core.Pieces;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;

namespace NoMercy.Plugin.TorrentDownloader.Core.Swarm;

/// <summary>
/// One torrent, running. This is where the coordinator's single-owner rule is made
/// real: every peer reads its own socket, but nothing touches the coordinator except
/// one loop draining one queue.
///
/// <para>
/// That is the whole concurrency design in one place. Peers are as parallel as the
/// network allows; the decisions they feed are serialised, so there is no lock to
/// contend on and no race to reproduce.
/// </para>
/// </summary>
public sealed class TorrentSession : IAsyncDisposable
{
    private readonly TorrentMetadata _metadata;
    private readonly IPieceStore _store;
    private readonly IResumeStore _resume;
    private readonly TorrentCoordinator _coordinator;
    private readonly Channel<PeerEvent> _events = Channel.CreateUnbounded<PeerEvent>();
    private readonly Dictionary<PeerKey, PeerConnection> _connections = [];
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Task> _peerLoops = [];

    private int _nextPeerHandle;
    private bool _disposed;
    private Task? _brain;

    public TorrentSession(
        TorrentMetadata metadata,
        IPieceStore store,
        IResumeStore resume,
        Bitfield have,
        SwarmPolicy policy)
    {
        _metadata = metadata;
        _store = store;
        _resume = resume;
        _coordinator = new TorrentCoordinator(metadata, have, policy);

        if (_coordinator.IsComplete)
            _completed.TrySetResult();
    }

    public Bitfield Have => _coordinator.Have;

    public bool IsComplete => _coordinator.IsComplete;

    /// <summary>Completes when every piece is in and verified.</summary>
    public Task Completion => _completed.Task;

    /// <summary>
    /// Starts the brain. Nothing is decided until this is running, because it is the
    /// only thing allowed to touch the coordinator.
    /// </summary>
    public Task RunAsync(CancellationToken ct)
    {
        _brain = Task.Run(() => BrainLoopAsync(ct), ct);
        return _brain;
    }

    /// <summary>
    /// Stops accepting work and waits for what is already decided to finish.
    ///
    /// <para>
    /// A piece that verified but has not been written yet is the gap where the resume
    /// invariant would break: the coordinator counts it as held while the disk does not
    /// have it. Draining here means a shutdown never leaves that gap, and it is why a
    /// caller must stop rather than simply dispose.
    /// </para>
    /// </summary>
    public async Task StopAsync()
    {
        _events.Writer.TryComplete();

        if (_brain is null)
            return;

        try
        {
            await _brain;
        }
        catch (OperationCanceledException)
        {
            // Shutting down while cancelled is the ordinary case, not a failure.
        }
    }

    public void AddPeer(PeerConnection connection, CancellationToken ct)
    {
        PeerKey key = new(Interlocked.Increment(ref _nextPeerHandle));

        lock (_connections)
            _connections[key] = connection;

        _peerLoops.Add(Task.Run(() => PeerLoopAsync(key, connection, ct), ct));
    }

    private async Task PeerLoopAsync(PeerKey key, PeerConnection connection, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                PeerMessage message = await connection.ReceiveAsync(ct);

                if (!_events.Writer.TryWrite(new PeerEvent(key, message)))
                    return;
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // A peer dropping is the steady state, not an incident. Tell the brain so
            // its outstanding blocks are released, and say nothing to anyone else.
            // TryWrite because the queue may already be closing around us.
            _events.Writer.TryWrite(new PeerEvent(key, null));
        }
    }

    private async Task BrainLoopAsync(CancellationToken ct)
    {
        await foreach (PeerEvent next in _events.Reader.ReadAllAsync(ct))
        {
            IReadOnlyList<CoordinatorAction> actions = next.Message switch
            {
                null => _coordinator.PeerDisconnected(next.Peer),
                BitfieldMessage bitfield => Announce(next.Peer, bitfield),
                Have have => _coordinator.PeerAnnouncedHave(next.Peer, have.PieceIndex),
                Unchoke => _coordinator.PeerUnchoked(next.Peer),
                Choke => _coordinator.PeerChoked(next.Peer),
                PieceBlock block => _coordinator.BlockReceived(next.Peer, block.PieceIndex, block.Begin, block.Block),
                _ => [],
            };

            foreach (CoordinatorAction action in actions)
                await PerformAsync(action, ct);

            if (_coordinator.IsComplete && _completed.TrySetResult())
                return;
        }
    }

    private IReadOnlyList<CoordinatorAction> Announce(PeerKey peer, BitfieldMessage message)
    {
        try
        {
            return _coordinator.PeerAnnouncedBitfield(peer, Bitfield.FromBytes(message.Payload, _metadata.PieceCount));
        }
        catch (ArgumentException)
        {
            // A bitfield of the wrong size or with the spare bits set is a peer lying
            // about what it holds. Drop it rather than believing part of it.
            return _coordinator.PeerDisconnected(peer);
        }
    }

    private async Task PerformAsync(CoordinatorAction action, CancellationToken ct)
    {
        switch (action)
        {
            case SendMessage send:
                await SendAsync(send, ct);
                break;

            case PieceReady ready:
                // Write, flush, then record. The resume file must never claim more than
                // the disk holds, so this order is the invariant and not a preference.
                await _store.WritePieceAsync(ready.PieceIndex, ready.Data, ct);
                await _store.FlushAsync(ct);
                await _resume.SaveAsync(_metadata, _coordinator.Have, ct);
                break;

            case BanPeer ban:
                await DropAsync(ban.Peer);
                break;

            case PieceRejected:
                // Already discarded by the coordinator. Nothing to undo on disk,
                // because nothing unverified is ever written.
                break;
        }
    }

    private async Task SendAsync(SendMessage send, CancellationToken ct)
    {
        PeerConnection? connection;

        lock (_connections)
            _connections.TryGetValue(send.Peer, out connection);

        if (connection is null)
            return;

        try
        {
            await connection.SendAsync(send.Message, ct);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            _events.Writer.TryWrite(new PeerEvent(send.Peer, null));
        }
    }

    private async Task DropAsync(PeerKey peer)
    {
        PeerConnection? connection;

        lock (_connections)
        {
            _connections.Remove(peer, out connection);
        }

        if (connection is not null)
            await connection.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Drain first: disposing the stores out from under a half-written piece is
        // exactly the race StopAsync exists to close.
        await StopAsync();

        List<PeerConnection> open;

        lock (_connections)
        {
            open = [.. _connections.Values];
            _connections.Clear();
        }

        foreach (PeerConnection connection in open)
            await connection.DisposeAsync();
    }

    /// <summary>A message from a peer, or its departure when the message is null.</summary>
    private readonly record struct PeerEvent(PeerKey Peer, PeerMessage? Message);
}

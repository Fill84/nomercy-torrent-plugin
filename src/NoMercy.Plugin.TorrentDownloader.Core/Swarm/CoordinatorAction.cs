// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Peers;

namespace NoMercy.Plugin.TorrentDownloader.Core.Swarm;

/// <summary>Identifies one peer to the coordinator. The caller assigns the handles.</summary>
public readonly record struct PeerKey(int Value);

/// <summary>
/// What the coordinator decided. It never performs any of these itself: it owns the
/// state and returns intentions, which is what makes it a state machine a test can
/// drive with no network, no disk and no timing.
/// </summary>
public abstract record CoordinatorAction;

public sealed record SendMessage(PeerKey Peer, PeerMessage Message) : CoordinatorAction;

/// <summary>A piece arrived complete and hashed correctly. Write it, then record it.</summary>
public sealed record PieceReady(int PieceIndex, byte[] Data) : CoordinatorAction;

/// <summary>A piece assembled but hashed wrong. It has already been discarded.</summary>
public sealed record PieceRejected(int PieceIndex) : CoordinatorAction;

public sealed record BanPeer(PeerKey Peer) : CoordinatorAction;

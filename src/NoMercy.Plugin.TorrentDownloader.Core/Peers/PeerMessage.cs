// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Peers;

public abstract record PeerMessage;

/// <summary>Zero length, no id. Sent to keep an idle connection from timing out.</summary>
public sealed record KeepAlive : PeerMessage;

public sealed record Choke : PeerMessage;

public sealed record Unchoke : PeerMessage;

public sealed record Interested : PeerMessage;

public sealed record NotInterested : PeerMessage;

public sealed record Have(int PieceIndex) : PeerMessage;

// The three messages below carry a byte array. A record compares arrays by reference,
// which would make two blocks holding identical bytes unequal - and "is this the block
// I asked for" is a question the coordinator and the tests both need answered by value.

public sealed record BitfieldMessage(byte[] Payload) : PeerMessage
{
    public bool Equals(BitfieldMessage? other) => other is not null && Payload.AsSpan().SequenceEqual(other.Payload);

    public override int GetHashCode() => Payload.Length;
}

public sealed record Request(int PieceIndex, int Begin, int Length) : PeerMessage;

public sealed record PieceBlock(int PieceIndex, int Begin, byte[] Block) : PeerMessage
{
    public bool Equals(PieceBlock? other) =>
        other is not null && PieceIndex == other.PieceIndex && Begin == other.Begin && Block.AsSpan().SequenceEqual(other.Block);

    public override int GetHashCode() => HashCode.Combine(PieceIndex, Begin, Block.Length);
}

public sealed record Cancel(int PieceIndex, int Begin, int Length) : PeerMessage;

/// <summary>The peer's DHT port. Part two listens for these.</summary>
public sealed record Port(int Listen) : PeerMessage;

/// <summary>BEP 10. Part two carries magnet metadata inside these.</summary>
public sealed record Extended(byte ExtensionId, byte[] Payload) : PeerMessage
{
    public bool Equals(Extended? other) =>
        other is not null && ExtensionId == other.ExtensionId && Payload.AsSpan().SequenceEqual(other.Payload);

    public override int GetHashCode() => HashCode.Combine(ExtensionId, Payload.Length);
}

public sealed class PeerProtocolException(string message) : Exception(message);

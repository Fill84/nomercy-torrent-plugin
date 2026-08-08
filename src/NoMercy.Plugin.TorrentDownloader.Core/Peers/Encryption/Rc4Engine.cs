// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Peers.Encryption;

/// <summary>
/// RC4, as MSE uses it.
///
/// <para>
/// RC4 is broken for anything that needs real confidentiality, and it is used here
/// for the one thing MSE actually asks of it: making the stream not look like
/// BitTorrent to equipment that shapes traffic by pattern. It is obfuscation with a
/// key, not protection of the content, and nothing else in this plugin should reach
/// for it.
/// </para>
/// </summary>
public sealed class Rc4Engine
{
    private readonly byte[] _state = new byte[256];
    private int _i;
    private int _j;

    public Rc4Engine(ReadOnlySpan<byte> key, int discardBytes)
    {
        if (key.IsEmpty)
            throw new ArgumentException("an RC4 key cannot be empty", nameof(key));

        ArgumentOutOfRangeException.ThrowIfNegative(discardBytes);

        for (int index = 0; index < 256; index++)
            _state[index] = (byte)index;

        int swap = 0;

        for (int index = 0; index < 256; index++)
        {
            swap = (swap + _state[index] + key[index % key.Length]) & 0xFF;
            (_state[index], _state[swap]) = (_state[swap], _state[index]);
        }

        // MSE throws away the first 1024 bytes of keystream. RC4's early output is
        // correlated with the key, which is the weakness that sank it everywhere else.
        for (int index = 0; index < discardBytes; index++)
            NextKeyByte();
    }

    /// <summary>XORs the buffer with the keystream, in place. Encrypting twice returns the original.</summary>
    public void Process(Span<byte> buffer)
    {
        for (int index = 0; index < buffer.Length; index++)
            buffer[index] ^= NextKeyByte();
    }

    private byte NextKeyByte()
    {
        _i = (_i + 1) & 0xFF;
        _j = (_j + _state[_i]) & 0xFF;

        (_state[_i], _state[_j]) = (_state[_j], _state[_i]);

        return _state[(_state[_i] + _state[_j]) & 0xFF];
    }
}

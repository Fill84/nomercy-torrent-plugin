// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Pieces;

/// <summary>
/// Which pieces are held. Used for our own progress, for what each peer advertises,
/// and for the resume record, so the wire format is the one BitTorrent defines:
/// piece zero is the high bit of the first byte.
/// </summary>
public sealed class Bitfield
{
    private readonly bool[] _bits;

    public Bitfield(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _bits = new bool[length];
    }

    public int Length => _bits.Length;

    public int SetCount { get; private set; }

    public bool IsComplete => SetCount == _bits.Length;

    public bool this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _bits.Length);
            return _bits[index];
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _bits.Length);

            if (_bits[index] == value)
                return;

            _bits[index] = value;
            SetCount += value ? 1 : -1;
        }
    }

    public IEnumerable<int> Missing()
    {
        for (int index = 0; index < _bits.Length; index++)
        {
            if (!_bits[index])
                yield return index;
        }
    }

    public byte[] ToBytes()
    {
        byte[] bytes = new byte[(_bits.Length + 7) / 8];

        for (int index = 0; index < _bits.Length; index++)
        {
            if (_bits[index])
                bytes[index / 8] |= (byte)(0b1000_0000 >> (index % 8));
        }

        return bytes;
    }

    public static Bitfield FromBytes(ReadOnlySpan<byte> bytes, int length)
    {
        int expected = (length + 7) / 8;

        if (bytes.Length != expected)
            throw new ArgumentException($"a bitfield for {length} pieces is {expected} bytes, not {bytes.Length}", nameof(bytes));

        Bitfield field = new(length);

        for (int index = 0; index < length; index++)
        {
            if ((bytes[index / 8] & (0b1000_0000 >> (index % 8))) != 0)
                field[index] = true;
        }

        // The spec requires the bits past the last piece to be zero. A peer that sets
        // them is claiming pieces that do not exist, and trusting the rest of its
        // bitfield after that is not warranted.
        for (int index = length; index < expected * 8; index++)
        {
            if ((bytes[index / 8] & (0b1000_0000 >> (index % 8))) != 0)
                throw new ArgumentException("the spare bits after the last piece are not zero", nameof(bytes));
        }

        return field;
    }
}

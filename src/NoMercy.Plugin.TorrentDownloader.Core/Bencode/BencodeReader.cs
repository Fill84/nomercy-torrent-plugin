// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text;

namespace NoMercy.Plugin.TorrentDownloader.Core.Bencode;

public static class BencodeReader
{
    public static BValue Parse(ReadOnlySpan<byte> input)
    {
        BValue value = Read(input, out int consumed);

        if (consumed != input.Length)
            throw new BencodeException($"trailing data after the value: {input.Length - consumed} bytes");

        return value;
    }

    public static BValue Read(ReadOnlySpan<byte> input, out int consumed)
    {
        if (input.IsEmpty)
            throw new BencodeException("empty input");

        return input[0] switch
        {
            (byte)'i' => ReadInteger(input, out consumed),
            (byte)'l' => ReadList(input, out consumed),
            (byte)'d' => ReadDictionary(input, out consumed),
            >= (byte)'0' and <= (byte)'9' => ReadBytes(input, out consumed),
            _ => throw new BencodeException($"unexpected byte 0x{input[0]:X2} at the start of a value"),
        };
    }

    private static BInteger ReadInteger(ReadOnlySpan<byte> input, out int consumed)
    {
        int end = input.IndexOf((byte)'e');

        if (end < 0)
            throw new BencodeException("integer is not terminated");

        ReadOnlySpan<byte> digits = input[1..end];

        if (digits.IsEmpty)
            throw new BencodeException("integer has no digits");

        // "i-0e" and any leading zero are invalid per the spec, and a client that
        // accepts them will compute a different info hash than everyone else.
        if (digits[0] == (byte)'0' && digits.Length > 1)
            throw new BencodeException("integer has a leading zero");

        if (digits is [(byte)'-', (byte)'0', ..])
            throw new BencodeException("negative zero");

        if (!long.TryParse(Encoding.ASCII.GetString(digits), out long value))
            throw new BencodeException($"'{Encoding.ASCII.GetString(digits)}' is not an integer");

        consumed = end + 1;
        return new BInteger(value);
    }

    private static BBytes ReadBytes(ReadOnlySpan<byte> input, out int consumed)
    {
        int colon = input.IndexOf((byte)':');

        if (colon < 0)
            throw new BencodeException("byte string has no length separator");

        if (!int.TryParse(Encoding.ASCII.GetString(input[..colon]), out int length) || length < 0)
            throw new BencodeException("byte string has an invalid length");

        int start = colon + 1;

        if (start + length > input.Length)
            throw new BencodeException($"byte string claims {length} bytes but only {input.Length - start} remain");

        consumed = start + length;
        return new BBytes(input.Slice(start, length).ToArray());
    }

    private static BList ReadList(ReadOnlySpan<byte> input, out int consumed)
    {
        List<BValue> items = [];
        int offset = 1;

        while (true)
        {
            if (offset >= input.Length)
                throw new BencodeException("list is not terminated");

            if (input[offset] == (byte)'e')
            {
                consumed = offset + 1;
                return new BList(items);
            }

            items.Add(Read(input[offset..], out int itemConsumed));
            offset += itemConsumed;
        }
    }

    private static BDictionary ReadDictionary(ReadOnlySpan<byte> input, out int consumed)
    {
        Dictionary<string, BValue> entries = [];
        int offset = 1;

        while (true)
        {
            if (offset >= input.Length)
                throw new BencodeException("dictionary is not terminated");

            if (input[offset] == (byte)'e')
            {
                consumed = offset + 1;
                return new BDictionary(entries);
            }

            BValue key = Read(input[offset..], out int keyConsumed);

            if (key is not BBytes keyBytes)
                throw new BencodeException("dictionary key is not a byte string");

            offset += keyConsumed;

            if (offset >= input.Length)
                throw new BencodeException("dictionary key has no value");

            entries[keyBytes.AsText()] = Read(input[offset..], out int valueConsumed);
            offset += valueConsumed;
        }
    }
}

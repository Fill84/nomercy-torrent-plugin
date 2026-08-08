// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text;

namespace NoMercy.Plugin.TorrentDownloader.Core.Bencode;

public abstract record BValue;

public sealed record BInteger(long Value) : BValue;

public sealed record BBytes(byte[] Value) : BValue
{
    public string AsText() => Encoding.UTF8.GetString(Value);
}

public sealed record BList(IReadOnlyList<BValue> Items) : BValue;

public sealed record BDictionary(IReadOnlyDictionary<string, BValue> Entries) : BValue;

public sealed class BencodeException(string message) : Exception(message);

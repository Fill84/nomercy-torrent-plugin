// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Diagnostics.CodeAnalysis;

namespace NoMercy.Plugin.TorrentDownloader.Core.Torrents;

/// <summary>
/// What a magnet link actually carries: an info hash, maybe a name, maybe some
/// trackers. Not the piece lengths and not the file list - those live with the
/// peers, and BEP 9 is how they are fetched.
/// </summary>
public sealed record MagnetLink(byte[] InfoHash, string? DisplayName, IReadOnlyList<string> Trackers)
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const int HashLength = 20;

    public static bool TryParse(string link, [NotNullWhen(true)] out MagnetLink? magnet)
    {
        try
        {
            magnet = Parse(link);
            return true;
        }
        catch (MetadataException)
        {
            magnet = null;
            return false;
        }
    }

    public static MagnetLink Parse(string link)
    {
        if (string.IsNullOrWhiteSpace(link) || !link.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            throw new MetadataException("that is not a magnet link");

        byte[]? infoHash = null;
        string? displayName = null;
        List<string> trackers = [];

        foreach (string pair in link["magnet:?".Length..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');

            if (equals <= 0)
                continue;

            string key = pair[..equals].ToLowerInvariant();
            string value = Uri.UnescapeDataString(pair[(equals + 1)..]);

            switch (key)
            {
                case "xt" when value.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase):
                    infoHash = ReadInfoHash(value["urn:btih:".Length..]);
                    break;

                case "dn":
                    displayName = value;
                    break;

                case "tr":
                    trackers.Add(value);
                    break;
            }
        }

        // Anything else - xl, ws, so - is either advisory or a feature this plugin
        // does not use. Ignoring an unknown parameter is how a magnet from a newer
        // site still works here.
        return infoHash is null
            ? throw new MetadataException("the magnet link carries no BitTorrent info hash")
            : new MagnetLink(infoHash, displayName, trackers);
    }

    private static byte[] ReadInfoHash(string value) => value.Length switch
    {
        40 => FromHex(value),

        // Older sites still hand out base32. It is the same twenty bytes, and a client
        // that only reads hex silently refuses half of what is out there.
        32 => FromBase32(value),

        _ => throw new MetadataException($"'{value}' is neither a 40 character hex nor a 32 character base32 info hash"),
    };

    private static byte[] FromHex(string value)
    {
        try
        {
            return Convert.FromHexString(value);
        }
        catch (FormatException)
        {
            throw new MetadataException($"'{value}' is not hexadecimal");
        }
    }

    private static byte[] FromBase32(string value)
    {
        byte[] bytes = new byte[HashLength];
        int buffer = 0;
        int bits = 0;
        int written = 0;

        foreach (char symbol in value.ToUpperInvariant())
        {
            int index = Base32Alphabet.IndexOf(symbol);

            if (index < 0)
                throw new MetadataException($"'{value}' is not base32");

            buffer = (buffer << 5) | index;
            bits += 5;

            if (bits < 8)
                continue;

            bits -= 8;
            bytes[written++] = (byte)(buffer >> bits);
        }

        return written == HashLength
            ? bytes
            : throw new MetadataException($"'{value}' decoded to {written} bytes rather than {HashLength}");
    }
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Security.Cryptography;
using NoMercy.Plugin.TorrentDownloader.Core.Bencode;

namespace NoMercy.Plugin.TorrentDownloader.Core.Torrents;

public static class MetadataParser
{
    private const int HashLength = 20;

    public static TorrentMetadata FromTorrentFile(ReadOnlySpan<byte> contents)
    {
        BValue root = BencodeReader.Parse(contents);

        if (root is not BDictionary dictionary)
            throw new MetadataException("a torrent file must be a dictionary");

        if (!dictionary.Entries.TryGetValue("info", out BValue? info) || info is not BDictionary infoDictionary)
            throw new MetadataException("the torrent has no info dictionary");

        return FromInfoDictionary(infoDictionary, ReadTrackers(dictionary));
    }

    public static TorrentMetadata FromInfoDictionary(BDictionary info, IReadOnlyList<string> trackers)
    {
        // The hash is over the re-encoded dictionary rather than the original slice, so
        // metadata reconstructed from peers (BEP 9) hashes identically to a .torrent file.
        byte[] infoHash = SHA1.HashData(BencodeWriter.Write(info));

        string name = Text(info, "name");
        long pieceLength = Integer(info, "piece length");

        if (pieceLength <= 0)
            throw new MetadataException("piece length must be positive");

        byte[] pieces = Bytes(info, "pieces");

        if (pieces.Length % HashLength != 0)
            throw new MetadataException($"the piece list is {pieces.Length} bytes, which is not a multiple of 20");

        List<byte[]> hashes = [];

        for (int offset = 0; offset < pieces.Length; offset += HashLength)
            hashes.Add(pieces[offset..(offset + HashLength)]);

        return new TorrentMetadata(infoHash, name, pieceLength, hashes, ReadFiles(info, name), trackers);
    }

    private static IReadOnlyList<FileEntry> ReadFiles(BDictionary info, string name)
    {
        if (info.Entries.TryGetValue("length", out BValue? single))
        {
            long length = single is BInteger integer
                ? integer.Value
                : throw new MetadataException("length must be an integer");

            return [new FileEntry([SafeComponent(name)], length, 0)];
        }

        if (!info.Entries.TryGetValue("files", out BValue? files) || files is not BList list)
            throw new MetadataException("the torrent has neither a length nor a files list");

        List<FileEntry> entries = [];
        long offset = 0;

        foreach (BValue item in list.Items)
        {
            if (item is not BDictionary file)
                throw new MetadataException("a file entry must be a dictionary");

            long length = Integer(file, "length");

            if (length < 0)
                throw new MetadataException("a file length cannot be negative");

            if (!file.Entries.TryGetValue("path", out BValue? path) || path is not BList components || components.Items.Count == 0)
                throw new MetadataException("a file entry needs a non-empty path");

            // The torrent name is the containing folder for a multi-file torrent.
            List<string> parts = [SafeComponent(name)];

            foreach (BValue component in components.Items)
            {
                if (component is not BBytes text)
                    throw new MetadataException("a path component must be a byte string");

                parts.Add(SafeComponent(text.AsText()));
            }

            entries.Add(new FileEntry(parts, length, offset));
            offset += length;
        }

        return entries;
    }

    /// <summary>
    /// A torrent is untrusted input, and a path component of ".." would write outside the
    /// download folder. Refuse rather than sanitise: a torrent that tries this is hostile,
    /// and quietly renaming its files would hide that.
    /// </summary>
    private static string SafeComponent(string component)
    {
        if (component is "" or "." or "..")
            throw new MetadataException($"'{component}' is not a usable path component");

        if (component.Contains('/') || component.Contains('\\') || component.Contains('\0'))
            throw new MetadataException($"'{component}' contains a path separator");

        if (component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new MetadataException($"'{component}' contains characters that are not valid in a file name");

        return component;
    }

    private static IReadOnlyList<string> ReadTrackers(BDictionary root)
    {
        List<string> trackers = [];

        if (root.Entries.TryGetValue("announce", out BValue? announce) && announce is BBytes single)
            trackers.Add(single.AsText());

        if (root.Entries.TryGetValue("announce-list", out BValue? tiers) && tiers is BList tierList)
        {
            foreach (BValue tier in tierList.Items)
            {
                if (tier is not BList urls)
                    continue;

                foreach (BValue url in urls.Items)
                {
                    if (url is BBytes text && !trackers.Contains(text.AsText()))
                        trackers.Add(text.AsText());
                }
            }
        }

        return trackers;
    }

    private static string Text(BDictionary dictionary, string key) =>
        dictionary.Entries.TryGetValue(key, out BValue? value) && value is BBytes bytes
            ? bytes.AsText()
            : throw new MetadataException($"'{key}' is missing or is not a byte string");

    private static long Integer(BDictionary dictionary, string key) =>
        dictionary.Entries.TryGetValue(key, out BValue? value) && value is BInteger integer
            ? integer.Value
            : throw new MetadataException($"'{key}' is missing or is not an integer");

    private static byte[] Bytes(BDictionary dictionary, string key) =>
        dictionary.Entries.TryGetValue(key, out BValue? value) && value is BBytes bytes
            ? bytes.Value
            : throw new MetadataException($"'{key}' is missing or is not a byte string");
}

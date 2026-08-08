// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text;

namespace NoMercy.Plugin.TorrentDownloader.Core.Bencode;

public static class BencodeWriter
{
    public static byte[] Write(BValue value)
    {
        MemoryStream buffer = new();
        Write(buffer, value);
        return buffer.ToArray();
    }

    private static void Write(Stream output, BValue value)
    {
        switch (value)
        {
            case BInteger integer:
                WriteAscii(output, $"i{integer.Value}e");
                break;

            case BBytes bytes:
                WriteAscii(output, $"{bytes.Value.Length}:");
                output.Write(bytes.Value);
                break;

            case BList list:
                WriteAscii(output, "l");
                foreach (BValue item in list.Items)
                    Write(output, item);
                WriteAscii(output, "e");
                break;

            case BDictionary dictionary:
                WriteAscii(output, "d");
                // Raw byte order, not culture-aware ordering. The info hash is the SHA-1
                // of these bytes, so a different sort is a different torrent.
                foreach (KeyValuePair<string, BValue> entry in dictionary.Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    Write(output, new BBytes(Encoding.UTF8.GetBytes(entry.Key)));
                    Write(output, entry.Value);
                }
                WriteAscii(output, "e");
                break;

            default:
                throw new BencodeException($"cannot write {value.GetType().Name}");
        }
    }

    private static void WriteAscii(Stream output, string text) => output.Write(Encoding.ASCII.GetBytes(text));
}

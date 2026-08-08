// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Security.Cryptography;
using System.Text;
using NoMercy.Plugin.TorrentDownloader.Core.Bencode;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

/// <summary>
/// Builds a real .torrent over real content, so tests assert against bytes a
/// client would actually receive rather than a fixture nobody can regenerate.
/// </summary>
public sealed class TorrentBuilder
{
    private readonly List<(string[] Path, byte[] Content)> _files = [];
    private readonly List<string> _trackers = ["http://tracker.test/announce"];
    private long _pieceLength = 16 * 1024;
    private string _name = "test-torrent";

    public TorrentBuilder WithPieceLength(long pieceLength)
    {
        _pieceLength = pieceLength;
        return this;
    }

    public TorrentBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    public TorrentBuilder WithFile(string path, byte[] content)
    {
        _files.Add((path.Split('/'), content));
        return this;
    }

    public TorrentBuilder WithFile(string path, string content) => WithFile(path, Encoding.UTF8.GetBytes(content));

    /// <summary>Every byte of every file, in order. This is what the pieces hash over.</summary>
    public byte[] Content() => [.. _files.SelectMany(file => file.Content)];

    public byte[] Build()
    {
        byte[] content = Content();
        List<byte> pieces = [];

        for (int offset = 0; offset < content.Length; offset += (int)_pieceLength)
        {
            int length = (int)Math.Min(_pieceLength, content.Length - offset);
            pieces.AddRange(SHA1.HashData(content.AsSpan(offset, length)));
        }

        Dictionary<string, BValue> info = new()
        {
            ["name"] = new BBytes(Encoding.UTF8.GetBytes(_name)),
            ["piece length"] = new BInteger(_pieceLength),
            ["pieces"] = new BBytes([.. pieces]),
        };

        if (_files.Count == 1 && _files[0].Path.Length == 1)
        {
            info["length"] = new BInteger(_files[0].Content.Length);
        }
        else
        {
            // The torrent name is the containing folder in a multi-file torrent, so the
            // per-file paths here are relative to it and must not repeat it.
            info["files"] = new BList([.. _files.Select(file => (BValue)new BDictionary(new Dictionary<string, BValue>
            {
                ["length"] = new BInteger(file.Content.Length),
                ["path"] = new BList([.. file.Path.Skip(1).Select(part => (BValue)new BBytes(Encoding.UTF8.GetBytes(part)))]),
            }))]);
        }

        return BencodeWriter.Write(new BDictionary(new Dictionary<string, BValue>
        {
            ["announce"] = new BBytes(Encoding.UTF8.GetBytes(_trackers[0])),
            ["info"] = new BDictionary(info),
        }));
    }

    public byte[] ExpectedInfoHash()
    {
        BDictionary root = (BDictionary)BencodeReader.Parse(Build());
        return SHA1.HashData(BencodeWriter.Write(root.Entries["info"]));
    }
}

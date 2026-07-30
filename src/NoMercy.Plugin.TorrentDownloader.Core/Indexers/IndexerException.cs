// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

public class IndexerException : Exception
{
    public IndexerException(string message)
        : base(message) { }

    public IndexerException(string message, int statusCode)
        : base(message) => StatusCode = statusCode;

    public IndexerException(string message, Exception inner)
        : base(message, inner) { }

    public int? StatusCode { get; }
}

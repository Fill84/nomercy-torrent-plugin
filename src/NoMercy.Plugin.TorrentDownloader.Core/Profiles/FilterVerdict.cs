// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public record FilterVerdict(bool Accepted, string Reason)
{
    public static FilterVerdict Accept() => new(true, "match");

    public static FilterVerdict Reject(string reason) => new(false, reason);
}

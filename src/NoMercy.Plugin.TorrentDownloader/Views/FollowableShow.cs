// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>A show the library knows about, and whether this plugin is following it.</summary>
public sealed record FollowableShow(int ShowId, string Title, bool Followed);

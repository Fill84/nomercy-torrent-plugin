// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Releases;

public record ParsedRelease
{
    public required string Title { get; init; }
    public EpisodeSlot? Episode { get; init; }
    public int? SeasonPack { get; init; }
    public Quality Quality { get; init; }
    public VideoCodec Codec { get; init; }
    public string? ReleaseGroup { get; init; }
    public bool IsProper { get; init; }
    public bool IsRepack { get; init; }
    public IReadOnlyList<string> Languages { get; init; } = [];
    public bool IsDualAudio { get; init; }

    /// <inheritdoc cref="LanguageTags.IsMultiLanguage"/>
    public bool IsMultiLanguage { get; init; }
}

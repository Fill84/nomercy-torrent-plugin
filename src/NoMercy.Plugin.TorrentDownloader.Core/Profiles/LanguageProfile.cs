// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public record LanguageProfile(
    IReadOnlyList<string> Required,
    IReadOnlyList<string> Preferred,
    IReadOnlyList<string> Forbidden,
    bool RequireDualAudio
)
{
    public static LanguageProfile EnglishOnly { get; } = new(["English"], [], [], false);
}

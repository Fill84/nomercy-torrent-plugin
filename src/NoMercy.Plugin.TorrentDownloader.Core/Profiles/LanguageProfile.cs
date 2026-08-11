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
    /// <summary>
    /// "Only", meaning only - a release carrying a second audio language is refused even
    /// when English is one of them.
    ///
    /// <para>
    /// <see cref="Required"/> on its own asks whether English is <em>among</em> the
    /// languages, which is a different question and the wrong one. <c>ITA.ENG</c> answers
    /// yes to it, and so did every <c>MULTI</c> release, because MULTI names no language at
    /// all and an untagged release is read as English. Both were grabbed for an
    /// English-only library and neither was watchable in it.
    /// </para>
    /// </summary>
    public bool RefuseForeignAudio { get; init; }

    public static LanguageProfile EnglishOnly { get; } =
        new(["English"], [], [], false) { RefuseForeignAudio = true };

    /// <summary>No language rule at all, for an owner whose library is not English.</summary>
    public static LanguageProfile Any { get; } = new([], [], [], false);
}

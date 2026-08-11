// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Releases;

/// <param name="IsMultiLanguage">
/// The release says outright that it carries more than one audio language - <c>MULTI</c>,
/// <c>MULTi3</c>, <c>DUBBED</c> - without naming which. Separate from
/// <paramref name="IsDualAudio"/>, which those words also set: a dual-audio anime release is
/// English plus the original and perfectly watchable, while MULTI is a repack for an audience
/// that is not this one. Kept apart so refusing the second does not refuse the first.
/// </param>
public record LanguageTags(IReadOnlyList<string> Languages, bool IsDualAudio, bool IsMultiLanguage);

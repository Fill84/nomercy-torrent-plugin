// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
//
// NoMercy MediaServer Automated Torrent Plugin 
// Created by Phillippe Pelzer https://github.com/Fill84
// -----------------------------------------------------------------------------

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NoMercy.Plugin.TorrentDownloader.Core.Releases;

public static partial class TitleMatcher
{
    // Kept short on purpose. Every entry here is a token the show name is
    // allowed to absorb, so a loose list reopens false matches.
    private static readonly HashSet<string> CountryCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "US",
        "UK",
        "AU",
        "CA",
        "NZ",
        "IE",
        "ZA",
    };

    [GeneratedRegex(@"^(19|20)\d{2}$")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex TokenSeparatorPattern();

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlphanumericPattern();

    public static string Normalize(string? text) =>
        NonAlphanumericPattern()
            .Replace(FoldDiacritics(text ?? string.Empty).ToLowerInvariant(), string.Empty);

    // Scene releases strip diacritics, so "Élite" arrives as "Elite". Without folding,
    // the ASCII-only separator class treats the accent itself as a separator and splits
    // "Pokémon" into "Pok" and "mon", which matches nothing.
    private static string FoldDiacritics(string text)
    {
        string decomposed = text.Normalize(NormalizationForm.FormD);
        StringBuilder builder = new(decomposed.Length);

        foreach (char character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    public static bool Matches(string? title, string? showName)
    {
        string text = title ?? string.Empty;
        string[] want = Tokenize(showName);
        if (want.Length == 0)
            return false;

        string[] have = Tokenize(ScopeBeforeMarker(text));
        if (have.Length == 0)
            return false;

        if (LeadsWithQualifiersOnly(have, want))
            return true;

        if (EndsWith(have, want))
            return true;

        return GluedLeadingMatch(have, want);
    }

    private static string ScopeBeforeMarker(string title) =>
        ReleaseNameParser.NameScopeBoundaryIndex(title) is int index ? title[..index] : title;

    private static string[] Tokenize(string? text) =>
        TokenSeparatorPattern()
            .Split(FoldDiacritics(text ?? string.Empty))
            .Where(token => token.Length > 0)
            .ToArray();

    private static bool IsQualifier(string token) =>
        YearPattern().IsMatch(token) || CountryCodes.Contains(token);

    private static bool LeadsWithQualifiersOnly(string[] have, string[] want)
    {
        if (have.Length < want.Length)
            return false;

        for (int index = 0; index < want.Length; index++)
        {
            if (!string.Equals(have[index], want[index], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        for (int index = want.Length; index < have.Length; index++)
        {
            if (!IsQualifier(have[index]))
                return false;
        }

        return true;
    }

    private static bool EndsWith(string[] have, string[] want)
    {
        if (have.Length < want.Length)
            return false;

        int offset = have.Length - want.Length;
        for (int index = 0; index < want.Length; index++)
        {
            if (!string.Equals(have[offset + index], want[index], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static bool GluedLeadingMatch(string[] have, string[] want)
    {
        string joined = Normalize(string.Concat(have));
        string wanted = Normalize(string.Concat(want));

        if (wanted.Length == 0 || !joined.StartsWith(wanted, StringComparison.Ordinal))
            return false;

        string remainder = joined[wanted.Length..];
        return remainder.Length == 0 || IsQualifier(remainder);
    }
}

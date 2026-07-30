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

using System.Text.RegularExpressions;

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

internal static class TermMatcher
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    // A term pattern is user-authored, so it can be malformed or pathological. Neither may
    // escape: an invalid pattern must not abort the search cycle, and a backtracking pattern
    // must not hang it. A pattern that cannot be evaluated is treated as not matching.
    public static bool IsMatch(string title, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        try
        {
            return Regex.IsMatch(
                title,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                MatchTimeout
            );
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

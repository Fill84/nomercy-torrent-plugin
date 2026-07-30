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

namespace NoMercy.Plugin.TorrentDownloader.Core.Releases;

public static partial class ReleaseNameParser
{
    [GeneratedRegex(@"s(\d{1,2})[\s._-]?e(\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisodePattern();

    [GeneratedRegex(@"(?<!\d)(\d{1,2})x(\d{1,3})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CrossPattern();

    [GeneratedRegex(@"season\s*(\d{1,2})\s*episode\s*(\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VerbosePattern();

    // The trailing \b is load-bearing: without it the digit group backtracks to a
    // single digit and the lookahead passes against the second one.
    [GeneratedRegex(@"\bs(\d{1,2})\b(?!\s*e\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonPackPattern();

    [GeneratedRegex(@"season\s*(\d{1,2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VerboseSeasonPackPattern();

    [GeneratedRegex(@"\bs\d{1,2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonTokenPattern();

    private static Match? EarliestEpisodeMatch(string? title)
    {
        string text = title ?? string.Empty;
        Match? earliest = null;

        foreach (Regex pattern in new[] { SeasonEpisodePattern(), CrossPattern(), VerbosePattern() })
        {
            Match match = pattern.Match(text);
            if (match.Success && (earliest is null || match.Index < earliest.Index))
                earliest = match;
        }

        return earliest;
    }

    public static int? EpisodeMarkerIndex(string? title) => EarliestEpisodeMatch(title)?.Index;

    // Shared by TitleMatcher (scopes the show name before this point) and
    // LanguageTagExtractor (scopes language tags after it), so a season pack with no
    // episode marker still separates the two: without the season-token fallback, both
    // callers fall back to the whole title and can misread the show name.
    public static int? NameScopeBoundaryIndex(string? title)
    {
        if (EpisodeMarkerIndex(title) is int episodeIndex)
            return episodeIndex;

        Match season = SeasonTokenPattern().Match(title ?? string.Empty);
        return season.Success ? season.Index : null;
    }

    public static EpisodeSlot? ParseEpisode(string? title)
    {
        Match? match = EarliestEpisodeMatch(title);
        if (match is null)
            return null;

        return new EpisodeSlot(
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value)
        );
    }

    public static int? ParseSeasonPack(string? title)
    {
        if (ParseEpisode(title) is not null)
            return null;

        string text = title ?? string.Empty;

        Match verbose = VerboseSeasonPackPattern().Match(text);
        if (verbose.Success)
            return int.Parse(verbose.Groups[1].Value);

        Match compact = SeasonPackPattern().Match(text);
        return compact.Success ? int.Parse(compact.Groups[1].Value) : null;
    }

    [GeneratedRegex(@"\b(2160p|4k|uhd)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Uhd2160Pattern();

    [GeneratedRegex(@"\b1080[pi]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Fhd1080Pattern();

    [GeneratedRegex(@"\b720[pi]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Hd720Pattern();

    [GeneratedRegex(@"\b576[pi]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Sd576Pattern();

    [GeneratedRegex(@"\b480[pi]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Sd480Pattern();

    [GeneratedRegex(@"\bremux\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemuxPattern();

    [GeneratedRegex(@"\b(blu[\s._-]?ray|bdrip|brrip|bdremux)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BluRayPattern();

    [GeneratedRegex(@"\bweb[\s._-]?dl\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WebDlPattern();

    [GeneratedRegex(@"\b(web[\s._-]?rip|web)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WebRipPattern();

    [GeneratedRegex(@"\b(hdtv|pdtv|sdtv)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HdtvPattern();

    [GeneratedRegex(@"\bdvd[\s._-]?rip\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DvdRipPattern();

    [GeneratedRegex(@"\b(telesync|\bts\b)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TelesyncPattern();

    [GeneratedRegex(@"\b(cam|camrip)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CamPattern();

    [GeneratedRegex(@"\b(x[\s.]?265|h[\s.]?265|hevc)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HevcPattern();

    [GeneratedRegex(@"\b(x[\s.]?264|h[\s.]?264|avc)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex H264Pattern();

    [GeneratedRegex(@"\bav1\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Av1Pattern();

    public static Quality ParseQuality(string? title)
    {
        string text = title ?? string.Empty;
        return new Quality(ParseResolution(text), ParseSource(text));
    }

    private static Resolution ParseResolution(string text)
    {
        if (Uhd2160Pattern().IsMatch(text))
            return Resolution.Uhd2160;
        if (Fhd1080Pattern().IsMatch(text))
            return Resolution.Fhd1080;
        if (Hd720Pattern().IsMatch(text))
            return Resolution.Hd720;
        if (Sd576Pattern().IsMatch(text))
            return Resolution.Sd576;
        if (Sd480Pattern().IsMatch(text))
            return Resolution.Sd480;
        return Resolution.Unknown;
    }

    private static ReleaseSource ParseSource(string text)
    {
        if (RemuxPattern().IsMatch(text))
            return ReleaseSource.Remux;
        if (BluRayPattern().IsMatch(text))
            return ReleaseSource.BluRay;
        if (WebDlPattern().IsMatch(text))
            return ReleaseSource.WebDl;
        if (WebRipPattern().IsMatch(text))
            return ReleaseSource.WebRip;
        if (HdtvPattern().IsMatch(text))
            return ReleaseSource.Hdtv;
        if (DvdRipPattern().IsMatch(text))
            return ReleaseSource.DvdRip;
        if (TelesyncPattern().IsMatch(text))
            return ReleaseSource.Telesync;
        if (CamPattern().IsMatch(text))
            return ReleaseSource.Cam;
        return ReleaseSource.Unknown;
    }

    public static VideoCodec ParseCodec(string? title)
    {
        string text = title ?? string.Empty;
        if (HevcPattern().IsMatch(text))
            return VideoCodec.H265;
        if (H264Pattern().IsMatch(text))
            return VideoCodec.H264;
        if (Av1Pattern().IsMatch(text))
            return VideoCodec.Av1;
        return VideoCodec.Unknown;
    }

    [GeneratedRegex(@"^\[([^\]]+)\]")]
    private static partial Regex FansubGroupPattern();

    [GeneratedRegex(@"-([A-Za-z0-9_]+)(?:\[[^\]]*\])?\s*$")]
    private static partial Regex SceneGroupPattern();

    [GeneratedRegex(@"\bproper\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProperPattern();

    [GeneratedRegex(@"\brepack\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepackPattern();

    public static string? ParseGroup(string? title)
    {
        string text = (title ?? string.Empty).Trim();

        Match fansub = FansubGroupPattern().Match(text);
        if (fansub.Success)
            return fansub.Groups[1].Value;

        Match scene = SceneGroupPattern().Match(text);
        return scene.Success ? scene.Groups[1].Value : null;
    }

    public static bool IsProper(string? title) => ProperPattern().IsMatch(title ?? string.Empty);

    public static bool IsRepack(string? title) => RepackPattern().IsMatch(title ?? string.Empty);

    public static ParsedRelease Parse(string? title)
    {
        string text = title ?? string.Empty;
        LanguageTags tags = LanguageTagExtractor.Extract(text);

        return new ParsedRelease
        {
            Title = text,
            Episode = ParseEpisode(text),
            SeasonPack = ParseSeasonPack(text),
            Quality = ParseQuality(text),
            Codec = ParseCodec(text),
            ReleaseGroup = ParseGroup(text),
            IsProper = IsProper(text),
            IsRepack = IsRepack(text),
            Languages = tags.Languages,
            IsDualAudio = tags.IsDualAudio,
        };
    }
}

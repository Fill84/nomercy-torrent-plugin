// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

/// <summary>
/// Ladders built from one answer: how good is good enough.
///
/// <para>
/// A full ladder is a list of rungs with a cutoff, which is the right shape for the
/// scorer and the wrong shape for a settings page - nobody wants to spell out four
/// rungs to say "1080p". This turns the one question an owner can answer into the
/// structure the decider needs.
/// </para>
/// </summary>
public static class QualityLadders
{
    // Ascending, and source-agnostic on purpose: a WEB-DL and a Blu-ray of the same
    // resolution sit on the same rung, so the scorer separates them on its other signals
    // rather than on a preference nobody stated.
    private static readonly Resolution[] Ascending =
    [
        Resolution.Sd480,
        Resolution.Sd576,
        Resolution.Hd720,
        Resolution.Fhd1080,
        Resolution.Uhd2160,
    ];

    /// <summary>
    /// Every rung up to and including <paramref name="maximum"/>, with the cutoff on top.
    ///
    /// <para>
    /// Above the maximum is left off the ladder rather than ranked below it. A rung that
    /// is not there cannot be chosen, which is what a maximum has to mean - ranked merely
    /// lower is a preference the scorer can talk itself out of when the seeders look good.
    /// </para>
    /// </summary>
    /// <summary>
    /// One rung, and nothing else on the ladder.
    ///
    /// <para>
    /// The strict reading of a quality setting, and the one torrent-feed takes: 1080p means
    /// 1080p rather than "1080p or anything below it". A ceiling sounds kinder and is not,
    /// because a 720p release of tonight's episode is usually the first one posted - so the
    /// ceiling quietly turns into the answer, and the owner ends up with the quality they
    /// did not choose.
    /// </para>
    ///
    /// <para>
    /// The cost is real and deliberate: an episode that exists only in 720p is never taken.
    /// That is the owner's call and it is a setting.
    /// </para>
    /// </summary>
    public static QualityLadder Only(Resolution resolution)
    {
        if (!Ascending.Contains(resolution))
            throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "not a resolution a ladder can be built from");

        QualityDefinition rung = new(NameOf(resolution), resolution, ReleaseSource.Unknown);

        return new QualityLadder([rung], rung.Name);
    }

    public static QualityLadder UpTo(Resolution maximum)
    {
        if (!Ascending.Contains(maximum))
            throw new ArgumentOutOfRangeException(nameof(maximum), maximum, "not a resolution a ladder can be built up to");

        List<QualityDefinition> rungs =
        [
            .. Ascending
                .TakeWhile(resolution => resolution <= maximum)
                .Select(resolution => new QualityDefinition(NameOf(resolution), resolution, ReleaseSource.Unknown)),
        ];

        return new QualityLadder(rungs, rungs[^1].Name);
    }

    /// <summary>
    /// What an owner typed, or <paramref name="fallback"/> when it cannot be read.
    ///
    /// <para>
    /// Never throws: this reads a stored setting, and a value written by a later version
    /// or edited by hand must not be what stops the plugin from running.
    /// </para>
    /// </summary>
    public static Resolution ParseResolution(string? text, Resolution fallback) =>
        text?.Trim().ToLowerInvariant() switch
        {
            "480p" => Resolution.Sd480,
            "576p" => Resolution.Sd576,
            "720p" => Resolution.Hd720,
            "1080p" => Resolution.Fhd1080,
            "2160p" or "4k" => Resolution.Uhd2160,
            _ => fallback,
        };

    public static string NameOf(Resolution resolution) =>
        resolution switch
        {
            Resolution.Sd480 => "480p",
            Resolution.Sd576 => "576p",
            Resolution.Hd720 => "720p",
            Resolution.Fhd1080 => "1080p",
            Resolution.Uhd2160 => "2160p",
            _ => "unknown",
        };
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public class ReleaseFilter
{
    private const double Gigabyte = 1024d * 1024d * 1024d;

    public FilterVerdict Evaluate(ReleaseInfo release, ParsedRelease parsed, FilterContext context)
    {
        ReleaseProfile profile = context.Profile;

        if (!TitleMatcher.Matches(release.Title, context.ShowName))
            return FilterVerdict.Reject($"show name \"{context.ShowName}\" does not lead or end the title scope");

        FilterVerdict slotVerdict = CheckSlot(parsed, context);
        if (!slotVerdict.Accepted)
            return slotVerdict;

        FilterVerdict languageVerdict = CheckLanguage(parsed, profile.Language);
        if (!languageVerdict.Accepted)
            return languageVerdict;

        if (parsed.ReleaseGroup is string group
            && profile.BlockedGroups.Contains(group, StringComparer.OrdinalIgnoreCase))
            return FilterVerdict.Reject($"blocked release group: {group}");

        FilterVerdict termVerdict = CheckTerms(release.Title, profile);
        if (!termVerdict.Accepted)
            return termVerdict;

        if (!profile.Quality.IsAllowed(parsed.Quality))
            return FilterVerdict.Reject($"quality {parsed.Quality} is not on the {profile.Name} ladder");

        FilterVerdict codecVerdict = CheckCodec(parsed, profile);
        if (!codecVerdict.Accepted)
            return codecVerdict;

        FilterVerdict sizeVerdict = CheckSize(release, profile);
        if (!sizeVerdict.Accepted)
            return sizeVerdict;

        if (profile.MinSeeders > 0 && release.Seeders < profile.MinSeeders)
            return FilterVerdict.Reject($"seeders {release.Seeders} below minimum {profile.MinSeeders}");

        return CheckBlacklist(release, context);
    }

    private static FilterVerdict CheckSlot(ParsedRelease parsed, FilterContext context)
    {
        if (context.WantedSlot is not EpisodeSlot slot)
            return FilterVerdict.Accept();

        if (parsed.Episode is EpisodeSlot found)
        {
            return found == slot
                ? FilterVerdict.Accept()
                : FilterVerdict.Reject($"release is {found}, not the wanted {slot}");
        }

        if (parsed.SeasonPack is int packSeason)
        {
            if (!context.Profile.AllowSeasonPacks)
                return FilterVerdict.Reject("season pack not allowed by profile");

            return packSeason == slot.Season
                ? FilterVerdict.Accept()
                : FilterVerdict.Reject($"season pack S{packSeason:00} is not season {slot.Season:00}");
        }

        return FilterVerdict.Reject("no episode or season number found in title");
    }

    private static FilterVerdict CheckCodec(ParsedRelease parsed, ReleaseProfile profile)
    {
        if (profile.Codec == VideoCodec.Unknown)
            return FilterVerdict.Accept();

        if (parsed.Codec == VideoCodec.Unknown)
            return profile.RequireCodecTag
                ? FilterVerdict.Reject(
                    $"release is untagged for codec and the {profile.Name} profile requires a codec tag ({profile.Codec})"
                )
                : FilterVerdict.Accept();

        if (parsed.Codec != profile.Codec)
            return FilterVerdict.Reject($"codec {parsed.Codec} is not the wanted {profile.Codec}");

        return FilterVerdict.Accept();
    }

    private static FilterVerdict CheckLanguage(ParsedRelease parsed, LanguageProfile language)
    {
        foreach (string forbidden in language.Forbidden)
        {
            if (parsed.Languages.Contains(forbidden, StringComparer.OrdinalIgnoreCase))
                return FilterVerdict.Reject($"forbidden language: {forbidden}");
        }

        if (language.Required.Count > 0
            && !language.Required.Any(required =>
                parsed.Languages.Contains(required, StringComparer.OrdinalIgnoreCase)))
            return FilterVerdict.Reject(
                $"language {string.Join("/", parsed.Languages)} is none of the required: "
                    + string.Join(", ", language.Required)
            );

        if (language.RefuseForeignAudio)
        {
            if (parsed.IsMultiLanguage)
                return FilterVerdict.Reject("a multi-language release, and only English was asked for");

            // Named alongside English rather than instead of it: ITA.ENG, FR.ENG. The
            // required check above passes these, because English really is in there.
            string[] besideEnglish =
            [
                .. parsed.Languages.Where(spoken =>
                    !language.Required.Contains(spoken, StringComparer.OrdinalIgnoreCase)),
            ];

            if (besideEnglish.Length > 0)
                return FilterVerdict.Reject(
                    $"carries {string.Join("/", besideEnglish)} audio beside English, and only English was asked for");
        }

        if (language.RequireDualAudio && !parsed.IsDualAudio)
            return FilterVerdict.Reject("not a dual audio release");

        return FilterVerdict.Accept();
    }

    private static FilterVerdict CheckTerms(string title, ReleaseProfile profile)
    {
        foreach (TermRule term in profile.Terms)
        {
            bool present = TermMatcher.IsMatch(title, term.Pattern);

            if (term.Kind == TermKind.Required && !present)
                return FilterVerdict.Reject($"required term missing: {term.Pattern}");

            if (term.Kind == TermKind.Forbidden && present)
                return FilterVerdict.Reject($"forbidden term present: {term.Pattern}");
        }

        return FilterVerdict.Accept();
    }

    // Reason strings are logged and compared verbatim regardless of the host's locale, so
    // the GB figures are formatted invariant rather than with the current culture's
    // decimal separator.
    private static FilterVerdict CheckSize(ReleaseInfo release, ReleaseProfile profile)
    {
        if (profile.MaxSizeBytes is long max && release.SizeBytes > max)
            return FilterVerdict.Reject(
                FormattableString.Invariant(
                    $"size {release.SizeBytes / Gigabyte:F1} GB over limit {max / Gigabyte:F1} GB"
                )
            );

        if (profile.MinSizeBytes is long min && release.SizeBytes > 0 && release.SizeBytes < min)
            return FilterVerdict.Reject(
                FormattableString.Invariant(
                    $"size {release.SizeBytes / Gigabyte:F1} GB under floor {min / Gigabyte:F1} GB"
                )
            );

        return FilterVerdict.Accept();
    }

    private static FilterVerdict CheckBlacklist(ReleaseInfo release, FilterContext context)
    {
        if (context.BlacklistedNormalisedTitles.Contains(TitleMatcher.Normalize(release.Title)))
            return FilterVerdict.Reject("this release title is blacklisted for this episode");

        if (release.InfoHash is string hash
            && context.BlacklistedInfoHashes.Contains(hash, StringComparer.OrdinalIgnoreCase))
            return FilterVerdict.Reject($"info hash {hash} is blacklisted for this episode");

        return FilterVerdict.Accept();
    }
}

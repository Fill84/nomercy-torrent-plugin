// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// What the plugin is refusing to pick, and a way to change its mind.
///
/// <para>
/// This list was invisible before it had a page. A release blacklisted for a fortnight is
/// the most likely reason an episode keeps not arriving, and an owner who cannot see the
/// list has no way to tell that from "nobody is seeding it" - two problems with completely
/// different answers.
/// </para>
/// </summary>
public static class SkippedView
{
    /// <summary>Beyond this it stops being a list and starts being a log.</summary>
    public const int Limit = 50;

    /// <summary>Entries expire on their own, so the page comes back to notice one that has.</summary>
    public const int RefreshSeconds = 60;

    public static PluginView Build(IReadOnlyList<BlacklistEntry> skipped) =>
        Pages.Page(
            Pages.Skipped,
            RefreshSeconds,
            Ui.Section(
                "skipped-releases",
                Format.Count("Skipped releases", skipped.Count),
                skipped.Count == 0
                    ? null
                    : "Passed over when choosing. Click one to allow it again.",
                Rows(skipped)));

    private static readonly PluginTableColumn[] Columns =
    [
        new() { Key = "release", Label = "Release" },
        new() { Key = "reason", Label = "Why" },
        new() { Key = "until", Label = "Until", Width = "9rem" },
    ];

    private static PluginComponent Rows(IReadOnlyList<BlacklistEntry> skipped)
    {
        if (skipped.Count == 0)
        {
            return Ui.EmptyState(
                "skipped-empty",
                "Nothing is being skipped",
                "A release is skipped after it fails or is cancelled. Most expire on their own.");
        }

        List<PluginComponent> rows = [];

        foreach (BlacklistEntry entry in skipped.OrderByDescending(entry => entry.AddedAt).Take(Limit))
        {
            rows.Add(Ui.TableRow(
                $"skipped-row-{entry.Handle}",
                new()
                {
                    ["release"] = entry.ReleaseTitle ?? entry.InfoHash ?? "an unnamed release",
                    ["reason"] = entry.Reason,
                    ["until"] = Format.Until(entry.ExpiresAt),
                },

                // Allowing one again is not destructive - it puts a release back among the
                // candidates and the next search decides for itself - so the row can carry
                // it without a confirmation standing in the way.
                PluginActionIntent.CallPlugin($"{PluginMethods.AllowRelease}/{entry.Handle}")));
        }

        return Ui.Table("skipped-list", Columns, rows);
    }
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Runtime.InteropServices;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// Google Chrome, if this machine already has it.
///
/// <para>
/// Chrome and nothing else - not Edge, not Chromium, not whatever the machine happens to
/// carry. Every one of those answers a challenge slightly differently, and a plugin that
/// drives Edge on one server and Chrome on the next produces results nobody can compare:
/// when it works for one owner and not another, the difference is invisible. One browser
/// everywhere is worth more than the megabytes saved by taking what is lying around.
/// </para>
///
/// <para>
/// The stable channel only. A path that exists is not the same as a browser that speaks the
/// DevTools protocol, and Chrome's beta and dev channels are a different build under a
/// different name.
/// </para>
///
/// <para>
/// When it is not here, the same Chrome is downloaded instead - so the engine is identical
/// either way and only the disk usage differs.
/// </para>
/// </summary>
public static class ChromeOnDisk
{
    public static string? Path()
    {
        foreach (string candidate in Candidates())
        {
            if (candidate.Length > 0 && File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (Environment.SpecialFolder root in new[]
                     {
                         Environment.SpecialFolder.ProgramFiles,
                         Environment.SpecialFolder.ProgramFilesX86,
                         Environment.SpecialFolder.LocalApplicationData,
                     })
            {
                yield return System.IO.Path.Combine(
                    Environment.GetFolderPath(root), "Google", "Chrome", "Application", "chrome.exe");
            }

            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";

            yield break;
        }

        yield return "/usr/bin/google-chrome-stable";
        yield return "/usr/bin/google-chrome";
        yield return "/opt/google/chrome/chrome";
    }
}

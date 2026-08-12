// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Runtime.InteropServices;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// A Chromium already on this machine, if there is one.
///
/// <para>
/// Asked before anything is downloaded. Nearly every desktop and most servers already carry
/// Chrome or Edge, and a plugin that fetches three hundred megabytes of a browser the
/// machine already has is a plugin nobody thanks. When there is genuinely none, the fetcher
/// downloads one - once, on the first challenge, rather than at install.
/// </para>
///
/// <para>
/// Only the stable channels of the two browsers Puppeteer actually drives. A path that
/// happens to exist is not the same as a browser that speaks the DevTools protocol, and
/// guessing wrong costs a failed solve on every challenge rather than an honest download.
/// </para>
/// </summary>
public static class InstalledBrowser
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
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            yield return System.IO.Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe");
            yield return System.IO.Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe");
            yield return System.IO.Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe");
            yield return System.IO.Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe");
            yield return System.IO.Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe");

            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
            yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
            yield return "/Applications/Chromium.app/Contents/MacOS/Chromium";

            yield break;
        }

        yield return "/usr/bin/google-chrome";
        yield return "/usr/bin/google-chrome-stable";
        yield return "/usr/bin/chromium";
        yield return "/usr/bin/chromium-browser";
        yield return "/usr/bin/microsoft-edge";
        yield return "/snap/bin/chromium";
    }
}

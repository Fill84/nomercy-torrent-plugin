// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader;

// The manifest and the IPlugin implementation must agree on all of this, and the
// host matches a loaded assembly to its manifest by id. A drift between the two
// is a plugin that either fails to load or loads as something it is not, so both
// sides read these constants and ManifestTests asserts they match the shipped json.
public static class PluginIdentity
{
    public static Guid Id { get; } = new("395df423-3e2f-4a1c-bc5b-dbc41a9133ef");

    public const string Name = "Torrent Downloader";

    public const string Description =
        "Keeps a TV library complete by downloading missing episodes over BitTorrent.";

    public static Version Version { get; } = new(0, 1, 0);

    public const string AssemblyFileName = "NoMercy.Plugin.TorrentDownloader.dll";
}

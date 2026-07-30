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

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

public static class Fixtures
{
    public static string Text(string name) => File.ReadAllText(Path(name));

    public static byte[] Bytes(string name) => File.ReadAllBytes(Path(name));

    private static string Path(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", name);
}

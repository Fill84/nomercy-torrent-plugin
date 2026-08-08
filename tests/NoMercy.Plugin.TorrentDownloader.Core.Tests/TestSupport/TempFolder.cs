// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "nm-torrent-tests",
        Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A locked handle on a build agent is not a test failure.
        }
    }
}

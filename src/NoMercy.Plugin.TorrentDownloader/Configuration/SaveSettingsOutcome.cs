// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Configuration;

// Success carries nothing back - the caller already has the settings it submitted -
// so the only thing worth exposing outside this file is whether it worked and, on
// failure, a message the owner can act on. Merged stays internal: it exists only to
// hand SettingsSaveHandler's own SaveAsync call a value, never to leak the merged
// settings back out to a controller that has no business reading them.
public sealed class SaveSettingsOutcome
{
    private SaveSettingsOutcome(bool succeeded, string? error, LoadedSettings? merged)
    {
        Succeeded = succeeded;
        Error = error;
        Merged = merged;
    }

    public bool Succeeded { get; }

    public string? Error { get; }

    internal LoadedSettings? Merged { get; }

    public static SaveSettingsOutcome Success(LoadedSettings merged) => new(true, null, merged);

    public static SaveSettingsOutcome Failure(string error) => new(false, error, null);
}

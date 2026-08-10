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
    private SaveSettingsOutcome(bool succeeded, string? error, LoadedSettings? merged, string? message = null)
    {
        Succeeded = succeeded;
        Error = error;
        Merged = merged;
        Message = message;
    }

    public bool Succeeded { get; }

    public string? Error { get; }

    /// <summary>
    /// What to tell the owner on success, when "Settings saved." would be a lie. The
    /// downloads page's buttons do not save settings - they pause a torrent or throw one
    /// away, and a toast that says otherwise is a toast that teaches the wrong thing.
    /// </summary>
    public string? Message { get; }

    internal LoadedSettings? Merged { get; }

    public static SaveSettingsOutcome Success(LoadedSettings merged) => new(true, null, merged);

    public static SaveSettingsOutcome Failure(string error) => new(false, error, null);

    /// <summary>Something that worked and was not a settings save.</summary>
    public static SaveSettingsOutcome Done(string message) => new(true, null, null, message);
}

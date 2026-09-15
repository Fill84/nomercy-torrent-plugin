using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

/// <summary>
/// Settings per show and preferences per library held in memory, worked out by the real
/// <see cref="EffectiveSettings.Of"/>.
/// </summary>
public sealed class FakeAppliedSettings : IAppliedSettings
{
    private readonly Dictionary<int, ShowSettings> _shows = [];

    private readonly Dictionary<string, LibraryPreferences> _libraries = new(StringComparer.Ordinal);

    private bool _everyShowOn;

    /// <summary>
    /// Every show not named otherwise switched on, saved and at 1080p, for a test about something other
    /// than which shows are searched.
    /// </summary>
    public static FakeAppliedSettings EveryShowOn()
    {
        return new() { _everyShowOn = true };
    }

    public FakeAppliedSettings Show(ShowSettings settings)
    {
        _shows[settings.ShowId] = settings;

        return this;
    }

    public FakeAppliedSettings Library(LibraryPreferences preferences)
    {
        _libraries[preferences.LibraryId] = preferences;

        return this;
    }

    public Task<EffectiveSettings> ForAsync(Show show, CancellationToken ct)
    {
        ShowSettings settings = _shows.GetValueOrDefault(show.Id)
                                ?? (_everyShowOn
                                    ? new ShowSettings(show.Id) { SwitchedOn = true, Saved = true, Quality = "1080p" }
                                    : new ShowSettings(show.Id));

        return Task.FromResult(EffectiveSettings.Of(
            settings,
            _libraries.GetValueOrDefault(show.LibraryId) ?? new LibraryPreferences(show.LibraryId)));
    }
}

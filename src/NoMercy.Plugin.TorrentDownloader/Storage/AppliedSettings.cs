using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;

namespace NoMercy.Plugin.TorrentDownloader.Storage;

/// <summary>A show's saved settings and its library's preferences, as <see cref="EffectiveSettings"/>.</summary>
public sealed class AppliedSettings(ShowSettingsRepository shows, LibraryPreferencesRepository libraries) : IAppliedSettings
{
    public async Task<EffectiveSettings> ForAsync(Show show, CancellationToken ct)
    {
        return EffectiveSettings.Of(
            await shows.ForAsync(show.Id, ct),
            await libraries.ForAsync(show.LibraryId, ct));
    }
}

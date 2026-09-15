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

    /// <summary>What applies to each of these shows, read once for a run.</summary>
    /// <remarks>
    /// Each library's preferences are read once however many of its shows there are: a run over a
    /// library of two hundred shows would otherwise ask the database four hundred times before any
    /// episode is worked on.
    /// </remarks>
    public async Task<SettingsByShow> ForShowsAsync(IEnumerable<Show> run, CancellationToken ct)
    {
        IReadOnlyDictionary<int, ShowSettings> saved = await shows.AllAsync(ct);
        Dictionary<string, LibraryPreferences> preferences = new(StringComparer.Ordinal);
        Dictionary<int, EffectiveSettings> byShow = [];

        foreach (Show show in run)
        {
            if (!preferences.TryGetValue(show.LibraryId, out LibraryPreferences? library))
            {
                library = await libraries.ForAsync(show.LibraryId, ct);
                preferences[show.LibraryId] = library;
            }

            byShow[show.Id] = EffectiveSettings.Of(saved.GetValueOrDefault(show.Id) ?? new ShowSettings(show.Id), library);
        }

        return new(byShow);
    }
}

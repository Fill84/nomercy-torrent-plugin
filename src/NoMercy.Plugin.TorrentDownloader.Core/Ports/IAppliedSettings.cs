using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Core.Ports;

/// <summary>What applies to one show, with its library's preferences counted in.</summary>
/// <remarks>
/// A port because the settings live in the plugin's database and Core references none. Read fresh on
/// every call: a show switched off on the overview is off for the next refresh and the next transfers
/// pass, with nothing held in between to go stale.
/// </remarks>
public interface IAppliedSettings
{
    Task<EffectiveSettings> ForAsync(Show show, CancellationToken ct);
}

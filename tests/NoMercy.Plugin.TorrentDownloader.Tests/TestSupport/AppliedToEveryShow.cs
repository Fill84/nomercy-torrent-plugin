using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>The same answer for every show: searched at 1080p, or not searched at all.</summary>
/// <remarks>
/// For tests about something other than which shows are searched. Which shows are, through the plugin's
/// own repositories, is <c>TheSwitchDecidesTests</c>.
/// </remarks>
public sealed class AppliedToEveryShow(bool searched) : IAppliedSettings
{
    public static AppliedToEveryShow Searched { get; } = new(true);

    public static AppliedToEveryShow NotSearched { get; } = new(false);

    public Task<EffectiveSettings> ForAsync(Show show, CancellationToken ct)
    {
        return Task.FromResult(EffectiveSettings.Of(
            new ShowSettings(show.Id) { SwitchedOn = searched, Quality = "1080p" },
            new LibraryPreferences(show.LibraryId)));
    }
}

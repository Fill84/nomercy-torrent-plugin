namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>What applies to each show of a run, read once at its start.</summary>
/// <remarks>
/// Read once because a run judges every name of every episode against it, and each read is a question
/// to the plugin's database. A show the run has nothing for has nothing set, which searches nothing.
/// </remarks>
public sealed class SettingsByShow(IReadOnlyDictionary<int, EffectiveSettings> byShow, EffectiveSettings? others = null)
{
    /// <summary>The same settings for every show, for a search cycle's own tests.</summary>
    public static SettingsByShow Every(EffectiveSettings settings)
    {
        return new(new Dictionary<int, EffectiveSettings>(), settings);
    }

    public EffectiveSettings For(int showId)
    {
        return byShow.GetValueOrDefault(showId) ?? others ?? new EffectiveSettings();
    }
}

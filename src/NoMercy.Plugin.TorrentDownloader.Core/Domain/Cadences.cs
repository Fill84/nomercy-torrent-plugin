namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>
/// When each of the four jobs runs. Defaults from docs/04-domain.md § Settings.
/// </summary>
/// <remarks>
/// Changing one of these changes nothing until the server restarts. Cadences
/// are registered once, when the plugin loads, and re-registering at runtime is
/// not something the host offers — so the settings page has to say so, or the
/// owner changes a cron, watches the old one keep firing, and concludes the
/// setting does not work.
/// </remarks>
public sealed class Cadences
{
    public string Transfers { get; set; } = "* * * * *";

    public string Feed { get; set; } = "*/15 * * * *";

    public string Search { get; set; } = "0 */6 * * *";

    public string Maintenance { get; set; } = "0 4 * * *";

    /// <summary>Each cadence with the name the owner sees for it.</summary>
    public IEnumerable<(string Name, string Expression)> All()
    {
        yield return ("transfers", Transfers);
        yield return ("feed", Feed);
        yield return ("search", Search);
        yield return ("maintenance", Maintenance);
    }
}

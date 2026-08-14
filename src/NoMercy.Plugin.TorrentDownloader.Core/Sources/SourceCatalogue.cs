namespace NoMercy.Plugin.TorrentDownloader.Core.Sources;

/// <summary>
/// Every source the plugin will actually ask, shipped and owner-configured
/// together.
/// </summary>
public sealed class SourceCatalogue
{
    private readonly List<SourceDefinition> _sources;

    private SourceCatalogue(List<SourceDefinition> sources)
    {
        _sources = sources;
    }

    /// <summary>Every source, enabled or not, in the order it will be asked.</summary>
    public IReadOnlyList<SourceDefinition> All => _sources;

    /// <summary>The ones that will actually be asked.</summary>
    public IEnumerable<SourceDefinition> Enabled => _sources.Where(source => source.Enabled);

    /// <summary>The enabled sources that can answer <paramref name="role"/>.</summary>
    public IEnumerable<SourceDefinition> For(SourceRole role)
    {
        return Enabled.Where(source => source.Role.HasFlag(role));
    }

    /// <summary>Every host the enabled sources reach, without duplicates.</summary>
    /// <remarks>What permission is actually asked for at runtime.</remarks>
    public IEnumerable<string> Hosts =>
        Enabled.SelectMany(source => source.Hosts).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every host in the catalogue, including those of sources that are off.</summary>
    /// <remarks>
    /// What the manifest has to declare. A manifest cannot change at runtime,
    /// so a source the owner switches on next month needs its host permitted
    /// today — otherwise enabling it produces a refusal that reads exactly like
    /// the site having nothing to offer.
    /// </remarks>
    public IEnumerable<string> EveryHost =>
        _sources.SelectMany(source => source.Hosts).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The catalogue the plugin runs on.
    /// </summary>
    /// <remarks>
    /// An owner's source with a shipped one's name replaces it, so a site whose
    /// address has moved can be corrected without waiting for a release. A
    /// shipped source the owner has switched off is dropped outright rather
    /// than kept and skipped, because a source that will never be asked has no
    /// business in a list of sources that will be.
    /// </remarks>
    public static SourceCatalogue Build(
        IEnumerable<SourceDefinition> shipped,
        IEnumerable<SourceDefinition> owner,
        IEnumerable<string> disabledShipped)
    {
        HashSet<string> disabled = new(disabledShipped, StringComparer.OrdinalIgnoreCase);
        SourceDefinition[] own = [.. owner];
        HashSet<string> replaced = new(own.Select(source => source.Name), StringComparer.OrdinalIgnoreCase);

        List<SourceDefinition> sources =
        [
            .. shipped.Where(source => !disabled.Contains(source.Name) && !replaced.Contains(source.Name)),
            .. own,
        ];

        return new(sources);
    }
}

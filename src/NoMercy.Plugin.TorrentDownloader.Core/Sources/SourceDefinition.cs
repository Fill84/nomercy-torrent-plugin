namespace NoMercy.Plugin.TorrentDownloader.Core.Sources;

/// <summary>How a search term is written into an address.</summary>
/// <remarks>
/// Declared per site and not derivable: TorrentGalaxy and 1337x both search
/// from their path and want opposite things.
/// </remarks>
public static class QueryStyles
{
    /// <summary>Punctuation to spaces, joined with <c>+</c>. The default.</summary>
    public const string Words = "words";

    /// <summary>Joined with <c>%20</c>, for a site searching from its path where a plus is a plus.</summary>
    public const string Spaced = "spaced";

    /// <summary>Lowercase, everything else to a single dash, for a site whose search is the path segment.</summary>
    public const string Slug = "slug";

    /// <summary>As given, for an endpoint matching a string rather than tokenising.</summary>
    public const string Verbatim = "verbatim";
}

/// <summary>
/// One source: where it is, how to ask it, and how often it will tolerate being
/// asked.
/// </summary>
/// <remarks>
/// The shipped ones are read from <c>sources.json</c> beside the assembly; the
/// owner's own come from the settings. Both become this, so nothing downstream
/// knows or cares which it is dealing with.
/// </remarks>
/// <param name="Name">Unique. An owner's source with a shipped one's name replaces it.</param>
/// <param name="Kind">What the reader and the role are chosen from.</param>
/// <param name="Url">
/// Its address. A feed's is read whole; a source with no separate search
/// address searches from this one, and it carries <c>{query}</c> if it can.
/// </param>
/// <param name="SearchUrl">
/// A separate search address, when the site has one. Its presence is what makes
/// a feed a name database as well.
/// </param>
/// <param name="Reader">Which per-site reader parses it, or null for the generic one.</param>
/// <param name="Query">One of <see cref="QueryStyles"/>.</param>
/// <param name="Gated">
/// Whether <see cref="Url"/> puts a challenge in the way, so it goes straight
/// to the browser.
/// </param>
/// <param name="SearchGated">
/// The same, for the search address. Two flags because gating is a property of
/// an <em>address</em> rather than a site: PreDB answers its feed over plain
/// HTTP and puts its search behind a challenge, and one flag for both would
/// either send every feed read through the browser or walk the search into a
/// challenge page every cycle.
/// </param>
/// <param name="Priority">
/// Higher is better. Used to choose between two copies that are otherwise
/// equally acceptable, seeders first.
/// </param>
/// <param name="MinimumIntervalSeconds">The smallest gap between two requests to this host.</param>
/// <param name="Enabled">Whether it is asked at all.</param>
/// <param name="Note">Why it behaves as it does, for whoever reads this next.</param>
public sealed record SourceDefinition(
    string Name,
    string Kind,
    string Url,
    string? SearchUrl = null,
    string? Reader = null,
    string Query = QueryStyles.Words,
    bool Gated = false,
    bool SearchGated = false,
    int Priority = 0,
    int MinimumIntervalSeconds = 15,
    bool Enabled = true,
    string? Note = null)
{
    /// <summary>What this source can answer.</summary>
    public SourceRole Role => SourceRoles.For(Kind, SearchUrl is not null);

    /// <summary>Whether it can be asked a question at all.</summary>
    public bool CanSearch => SearchUrl is not null || Url.Contains("{query}", StringComparison.Ordinal);

    /// <summary>
    /// The address a search goes to, or null when this source takes no question.
    /// </summary>
    public string? SearchAddress => SearchUrl ?? (Url.Contains("{query}", StringComparison.Ordinal) ? Url : null);

    /// <summary>
    /// Every host this source reaches, without duplicates.
    /// </summary>
    /// <remarks>
    /// Both addresses, because they are not always the same host and the search
    /// one is the half that gets forgotten: on a default install 0.3.4 requested
    /// permission for no host at all, and every refusal read like the site
    /// having nothing to offer.
    /// </remarks>
    public IEnumerable<string> Hosts
    {
        get
        {
            foreach (string? address in (string?[])[Url, SearchUrl])
            {
                if (HostOf(address) is string host)
                {
                    yield return host;
                }
            }
        }
    }

    /// <summary>The host part of an address, or null when it is not one.</summary>
    public static string? HostOf(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        // The placeholder is not legal in a URI, so it goes before parsing. It
        // never appears in the host part of any address this plugin uses.
        string parsable = address.Replace("{query}", "q", StringComparison.Ordinal);

        return Uri.TryCreate(parsable, UriKind.Absolute, out Uri? uri) ? uri.Host : null;
    }
}

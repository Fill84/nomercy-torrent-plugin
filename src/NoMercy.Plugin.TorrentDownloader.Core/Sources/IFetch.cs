namespace NoMercy.Plugin.TorrentDownloader.Core.Sources;

/// <summary>
/// What came back, or why nothing did.
/// </summary>
/// <remarks>
/// One or the other, never both and never neither. A result that could be an
/// empty body with no failure is one a reader would parse as "the site has
/// nothing", which is how a whole source went missing for weeks in 0.3.4.
/// </remarks>
public sealed record FetchResult
{
    private FetchResult(string? body, FetchFailure? failure)
    {
        Body = body;
        Failure = failure;
    }

    /// <summary>The body exactly as the host sent it, when there is one.</summary>
    public string? Body { get; }

    public FetchFailure? Failure { get; }

    public bool Ok => Failure is null;

    public static FetchResult Fetched(string body)
    {
        return new(body, null);
    }

    public static FetchResult Failed(FetchFailure failure)
    {
        return new(null, failure);
    }
}

/// <summary>
/// Getting a page, whatever it takes to get it.
/// </summary>
/// <remarks>
/// The whole of "whatever it takes" is behind this: the host gate, the grant,
/// plain HTTP, the browser for a gated address, and a challenge met once. A
/// stage asks for an address and is given a body or a reason.
/// </remarks>
public interface IFetch
{
    /// <summary>
    /// Fetches <paramref name="address"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="gated"/> comes from the catalogue rather than from
    /// discovery: finding out by trying costs a guaranteed refusal before every
    /// fetch of that host. It is a property of the address, not the site.
    /// </remarks>
    Task<FetchResult> GetAsync(Uri address, bool gated, CancellationToken ct);
}

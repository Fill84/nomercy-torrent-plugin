using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using NoMercy.Plugin.TorrentDownloader.Hosting;

namespace NoMercy.Plugin.TorrentDownloader.Tools.SourceHealth;

/// <summary>What walking one source through the real chain found.</summary>
public enum SourceCondition
{
    /// <summary>It answered, and the reader read what it answered.</summary>
    Answering,

    /// <summary>
    /// It answered, and what it answered was nothing. An answer, not a fault:
    /// Nyaa has nothing for anything that is not anime and says so honestly.
    /// </summary>
    NothingToSay,

    /// <summary>
    /// Rows, and not one of them offers any way to reach a torrent. Worth
    /// exactly as much as no rows at all — 0.3.4 wrote the detail address and
    /// read it nowhere, and TorrentBay produced rows for weeks and no downloads.
    /// </summary>
    NoRoute,

    /// <summary>
    /// The page is covered in releases and the reader saw none of them. The
    /// case this tool exists for: the site changed and the reader has not.
    /// </summary>
    BrokenReader,

    /// <summary>
    /// Asked twice and refused both times for asking too often. This plugin's
    /// pacing, not the site being broken, and <strong>G2</strong> is that the
    /// two were once the same sentence.
    /// </summary>
    RateLimited,

    /// <summary>Nothing came back at all, and the reason says what happened.</summary>
    NoAnswer,

    /// <summary>
    /// Nothing can read it. <strong>C4</strong>: not a site with nothing to
    /// offer and not a broken reader either — there is no reader.
    /// </summary>
    NoReader,

    /// <summary>
    /// It answered, the reader read it, and it saw fewer rows than last time.
    /// The quiet half of a broken reader: nought rows says so loudly, and three
    /// where there were forty says nothing at all without something to compare
    /// against.
    /// </summary>
    FewerRows,
}

/// <summary>
/// One source, asked a real release name, and what became of the answer.
/// </summary>
/// <param name="Source">Which source this is about.</param>
/// <param name="Condition">What was found.</param>
/// <param name="Address">Where it was asked, safe to print.</param>
/// <param name="Rows">How many rows the reader saw, or null when nothing was read.</param>
/// <param name="Releases">How many releases the page appears to carry, whether or not the reader saw them.</param>
/// <param name="Retried">Whether it had to be asked a second time.</param>
/// <param name="Page">
/// Exactly what the source sent, or null when it sent nothing. Repairing a
/// reader means reading the page it failed on, so the page is kept rather than
/// summarised.
/// </param>
/// <param name="Detail">What to tell whoever reads the report.</param>
public sealed record SourceHealthCheck(
    SourceDefinition Source,
    SourceCondition Condition,
    string Address,
    int? Rows,
    int? Releases,
    bool Retried,
    string? Page,
    string Detail)
{
    /// <summary>Whether this one needs somebody to do something.</summary>
    public bool Flagged => Condition is not (SourceCondition.Answering or SourceCondition.NothingToSay);
}

/// <summary>
/// Walks a source through the same catalogue, the same fetch and the same
/// reader the plugin uses, and says what happened.
/// </summary>
/// <remarks>
/// The judgement lives here and not in <c>Program</c> so it can be put to pages
/// the sources really sent. A health check that can only be exercised against
/// the live internet is one nobody can prove wrong, and 0.3.4's was.
/// </remarks>
public sealed class SourceCheck(ChallengeAwareFetch fetch, Readers readers)
{
    public async Task<SourceHealthCheck> RunAsync(SourceDefinition source, string term, CancellationToken ct)
    {
        // G2, before anything else. The fetch holds the last body it saw so the
        // report can print the page a source failed on; 0.3.4 never let go of
        // it, so a source that answered nothing at all was reported with the
        // previous source's page underneath it. Cleared on the way in rather
        // than on the way out, because a run that throws leaves no way out.
        fetch.LastBody = null;

        bool searching = source.SearchAddress is not null;
        Uri address = new(searching ? Query.Write(source.SearchAddress!, term, source.Query) : source.Url);

        // Gating is a property of an address, and the source knows which of its
        // flags belongs to which address. PreDB answers its feed over plain
        // HTTP and puts its search behind a challenge; 1337x has one address
        // and one flag.
        bool gated = searching ? source.SearchAddressGated : source.Gated;

        FetchResult result = await fetch.GetAsync(address, gated, ct);
        bool retried = false;

        if (result.Failure?.Outcome == FetchOutcome.RateLimited)
        {
            // Being told to slow down is this plugin's own doing. The gate has
            // already widened the gap for this host, so the second ask waits it
            // out on the way in rather than hammering straight back.
            retried = true;
            fetch.LastBody = null;
            result = await fetch.GetAsync(address, gated, ct);
        }

        string? page = result.Body ?? fetch.LastBody;
        int? releases = page is null ? null : PageReleases.CountIn(page);

        if (result.Failure is FetchFailure failure)
        {
            return new(
                source,
                failure.Outcome == FetchOutcome.RateLimited ? SourceCondition.RateLimited : SourceCondition.NoAnswer,
                failure.Address,
                null,
                releases,
                retried,
                page,
                failure.Reason);
        }

        ISourceReader? reader = readers.For(source);

        if (reader is null)
        {
            return new(
                source,
                SourceCondition.NoReader,
                Addresses.Redact(address),
                null,
                releases,
                retried,
                page,
                $"It answered, and nothing here reads a source of kind '{source.Kind}'"
                + (source.Reader is null ? "." : $" or a reader called '{source.Reader}'."));
        }

        IReadOnlyList<SourceRow> rows = reader.Read(result.Body!, address);
        SourceCondition condition = Judge(source, rows, releases ?? 0);

        return new(
            source,
            condition,
            Addresses.Redact(address),
            rows.Count,
            releases,
            retried,
            page,
            Say(condition, rows.Count, releases ?? 0));
    }

    /// <summary>
    /// What rows off a page mean, given what the page appears to carry.
    /// </summary>
    /// <remarks>
    /// Separate from the fetching so it can be put to real rows read off real
    /// captures.
    /// </remarks>
    public static SourceCondition Judge(SourceDefinition source, IReadOnlyList<SourceRow> rows, int releases)
    {
        if (rows.Count == 0)
        {
            // The whole distinction. A page with releases all over it and no
            // rows read off it is the reader being wrong about the site; a page
            // with nothing on it is a site with nothing, which is an answer.
            return releases > PageReleases.Few ? SourceCondition.BrokenReader : SourceCondition.NothingToSay;
        }

        // Only asked of a source that is supposed to answer torrents. A name
        // database answers names and reaches nothing, which is the point of it:
        // only a full release name is fit to put to an indexer.
        return source.Role.HasFlag(SourceRole.Indexer) && !rows.Any(HasRoute)
            ? SourceCondition.NoRoute
            : SourceCondition.Answering;
    }

    /// <summary>Whether a row offers any way at all to reach the torrent.</summary>
    /// <remarks>
    /// A detail page counts. No shipped indexer publishes a magnet on its
    /// listing — not one of the nine captured — so insisting on one here would
    /// flag every source this plugin has.
    /// </remarks>
    private static bool HasRoute(SourceRow row)
    {
        return row.Magnet is not null || row.InfoHash is not null || row.DetailUrl is not null;
    }

    private static string Say(SourceCondition condition, int rows, int releases)
    {
        return condition switch
        {
            SourceCondition.Answering => $"{rows} rows read.",
            SourceCondition.NothingToSay =>
                "It answered with nothing, and the page has nothing on it. Not a fault.",
            SourceCondition.NoRoute =>
                $"{rows} rows read and not one of them offers a way to the torrent.",
            SourceCondition.BrokenReader =>
                $"The page carries {releases} releases and the reader read none of them. Take a fresh capture and repair the reader against it.",
            _ => $"{rows} rows read.",
        };
    }
}

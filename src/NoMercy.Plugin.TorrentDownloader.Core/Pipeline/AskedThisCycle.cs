using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// What each site has already answered this cycle, so no question is put twice.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/specs/run.md</c>: a question already asked of an indexer during a run
/// is not asked of it again in that run. Per site, because "this name was asked"
/// is only true of the site it was asked of.
/// </para>
/// <para>
/// And per form. A name asked letter for letter and the same name asked without
/// its punctuation are two different questions, and a site that answered one
/// with nothing may well answer the other.
/// </para>
/// <para>
/// The saving is real. Eight gaps of one season once had every indexer asked the
/// identical question eight times, and apibay — which rate-limits hard — answered
/// 429 to the ninth.
/// </para>
/// </remarks>
public sealed class AskedThisCycle
{
    private readonly Dictionary<(string Source, SearchTerm Term), ReleaseCopy[]> _answers = [];
    private readonly HashSet<string> _sittingOut = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>Whether this site is left out of the rest of the run.</summary>
    public bool SitsOut(string source)
    {
        lock (_lock)
        {
            return _sittingOut.Contains(source);
        }
    }

    /// <summary>Leaves a site out of the rest of the run.</summary>
    /// <remarks>
    /// <c>docs/specs/run.md</c>: a site that still gives no answer after its second attempt — the fetch made
    /// both — is left out of the rest of that run. Asked again for the next name or the next episode it would
    /// cost two more attempts each time, and a run over thirty episodes would wait out a dead site sixty times.
    /// </remarks>
    public void SitOut(string source)
    {
        lock (_lock)
        {
            _sittingOut.Add(source);
        }
    }

    /// <summary>What that site said to that question, or null where it was never asked.</summary>
    public ReleaseCopy[]? Recall(string source, SearchTerm term)
    {
        lock (_lock)
        {
            return _answers.GetValueOrDefault((source, term));
        }
    }

    /// <summary>Remembers what one site answered, empty answers included.</summary>
    /// <remarks>
    /// An empty answer is worth remembering: asking again to be told the same
    /// nothing is the request this class exists to save.
    /// </remarks>
    public void Keep(string source, SearchTerm term, ReleaseCopy[] rows)
    {
        lock (_lock)
        {
            _answers[(source, term)] = rows;
        }
    }
}

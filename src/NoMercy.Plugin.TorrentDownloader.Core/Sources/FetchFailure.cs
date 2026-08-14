using System.Text.RegularExpressions;

namespace NoMercy.Plugin.TorrentDownloader.Core.Sources;

/// <summary>Why a fetch did not produce a body.</summary>
public enum FetchOutcome
{
    /// <summary>
    /// The server has not granted this host. Our fault, not the site's: it
    /// earns no backoff and is not a failure of the source.
    /// </summary>
    NotPermitted,

    /// <summary>The host said it has had enough — 429, 503, 509.</summary>
    RateLimited,

    /// <summary>The host answered, and the answer was no.</summary>
    Refused,

    /// <summary>Nothing answered: no route, no name, no listener, no patience.</summary>
    Unreachable,

    /// <summary>
    /// A challenge stood in the way and could not be cleared. Not the site
    /// refusing us — a site this plugin cannot read today.
    /// </summary>
    Challenged,

    /// <summary>
    /// The address needs a browser and there is none. Distinct from a refusal
    /// so the owner is told what to fix rather than blaming the site.
    /// </summary>
    NoBrowser,
}

/// <summary>
/// A fetch that produced no body, and why, in words fit for a page.
/// </summary>
/// <remarks>
/// <strong>G1.</strong> Every failure names the address it failed on. 0.3.4's
/// said "search returned HTTP 429" and left nobody able to tell which of
/// seventeen sources meant it — and when the address was added it published the
/// owner's API key into the log. Both halves are fixed here: the address is
/// always there, and it is always redacted.
/// </remarks>
/// <param name="Outcome">What kind of no it was.</param>
/// <param name="Address">Where, with any secret in it blanked.</param>
/// <param name="Reason">What to tell the owner.</param>
public sealed record FetchFailure(FetchOutcome Outcome, string Address, string Reason)
{
    public static FetchFailure For(FetchOutcome outcome, Uri address, string reason)
    {
        return new(outcome, Addresses.Redact(address), reason);
    }

    public override string ToString()
    {
        return $"{Reason} ({Address})";
    }
}

/// <summary>Making an address safe to say out loud.</summary>
public static class Addresses
{
    /// <summary>
    /// The parameter names whose values are secrets.
    /// </summary>
    /// <remarks>
    /// From docs/05-sources.md § Fetching, and it is a list of names rather
    /// than a search for secret-looking values: a passkey is forty hex
    /// characters and so is an info hash, and blanking every one of those would
    /// make the addresses useless for working out what went wrong.
    /// </remarks>
    private static readonly Regex Secret = new(
        "api_?key|apikey|passkey|token|secret|rss_?key",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>What is written in place of a secret.</summary>
    public const string Blanked = "***";

    /// <summary>
    /// <paramref name="address"/> with the value of every secret-named query
    /// parameter replaced.
    /// </summary>
    /// <remarks>
    /// The name is kept and only the value goes, because "which parameter did
    /// it have" is worth knowing and "what was in it" is exactly what must not
    /// be. An address that will not parse is blanked whole rather than passed
    /// through: not being able to read it is not a reason to publish it.
    /// </remarks>
    public static string Redact(Uri? address)
    {
        if (address is null)
        {
            return "(no address)";
        }

        string text = address.ToString();
        int question = text.IndexOf('?', StringComparison.Ordinal);

        if (question < 0)
        {
            return text;
        }

        string query = text[(question + 1)..];
        string[] parts = query.Split('&');

        for (int index = 0; index < parts.Length; index++)
        {
            int equals = parts[index].IndexOf('=', StringComparison.Ordinal);
            string name = equals < 0 ? parts[index] : parts[index][..equals];

            if (Secret.IsMatch(name))
            {
                parts[index] = $"{name}={Blanked}";
            }
        }

        return $"{text[..question]}?{string.Join('&', parts)}";
    }
}

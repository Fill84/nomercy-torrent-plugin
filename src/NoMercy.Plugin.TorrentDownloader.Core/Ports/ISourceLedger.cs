namespace NoMercy.Plugin.TorrentDownloader.Core.Ports;

/// <summary>
/// What one source last answered when it was asked.
/// </summary>
/// <param name="Name">The site, as the catalogue names it.</param>
/// <param name="At">When it was asked.</param>
/// <param name="Rows">How many releases it answered with.</param>
/// <param name="Refusal">
/// What it said when it refused, in its own words, or null when it did not
/// refuse. Nought rows and a refusal are two different answers: a site that
/// answered and had nothing is a working site, and telling the two apart is
/// what the Sources page exists for.
/// </param>
/// <param name="Duration">How long it took.</param>
public sealed record SourceAnswer(
    string Name,
    DateTimeOffset At,
    int Rows,
    string? Refusal,
    TimeSpan Duration);

/// <summary>
/// Where every ask is written down, so a page can say what a site really did.
/// </summary>
/// <remarks>
/// The journal says what is happening now and is bounded; this is the last
/// answer per source and survives a restart. Without it the Sources page could
/// only say what each source was configured to be, which is the one thing the
/// owner already knows.
/// </remarks>
public interface ISourceLedger
{
    /// <summary>Writes down what a source answered, replacing its last.</summary>
    Task RecordAsync(SourceAnswer answer, CancellationToken ct);
}

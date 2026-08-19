using System.Text.Json;

namespace NoMercy.Plugin.TorrentDownloader.Tools.SourceHealth;

/// <summary>
/// What each source answered with last time.
/// </summary>
/// <remarks>
/// <para>
/// The rule this exists for is "fewer rows than last time". Nought rows off a
/// page covered in releases is already a broken reader and says so loudly;
/// three rows where there were forty last week is a site that changed half its
/// markup, and every other condition calls that "answering". It is the quiet
/// half of the same fault.
/// </para>
/// <para>
/// Against the last run and never against a figure written down by hand: what a
/// search really returns depends on the term and the day, and a number chosen
/// here would be wrong for every source but the one it was measured on. A run
/// that flags a source once and then settles is the rule working — the new
/// count becomes the thing the next run is judged against.
/// </para>
/// </remarks>
public sealed record HealthBaseline(IReadOnlyDictionary<string, int> Rows)
{
    /// <summary>Where it lives, beside the report it explains.</summary>
    public const string FileName = "baseline.json";

    /// <summary>
    /// What this run should be remembered as.
    /// </summary>
    /// <remarks>
    /// Only what answered. A source that was rate-limited answered no rows, and
    /// writing nought down would make the next run — and every run after it —
    /// look like an improvement on a source nobody has heard from.
    /// </remarks>
    public static HealthBaseline Of(IReadOnlyList<SourceHealthCheck> checks)
    {
        Dictionary<string, int> rows = new(StringComparer.OrdinalIgnoreCase);

        foreach (SourceHealthCheck check in checks)
        {
            if (check.Rows is int seen && check.Condition is SourceCondition.Answering)
            {
                rows[check.Source.Name] = seen;
            }
        }

        return new(rows);
    }

    /// <summary>
    /// The same check, flagged when it saw fewer rows than last time.
    /// </summary>
    /// <remarks>
    /// A source nobody has a baseline for has nothing to be fewer than: the
    /// first run after a source is added would otherwise flag it for having no
    /// history. Only a source that answered is judged — one that refused has
    /// its own condition and saying it also has too few rows would bury it.
    /// </remarks>
    public static SourceHealthCheck Judge(SourceHealthCheck check, HealthBaseline was)
    {
        if (check.Condition is not SourceCondition.Answering
            || check.Rows is not int rows
            || !was.Rows.TryGetValue(check.Source.Name, out int before)
            || rows >= before)
        {
            return check;
        }

        // Both numbers. "Fewer rows" cannot tell a site that dropped two from
        // one that dropped every row but three, and only the second is worth
        // getting out of bed for.
        return check with
        {
            Condition = SourceCondition.FewerRows,
            Detail = $"{rows} rows, and {before} last time. Its page may have changed.",
        };
    }

    /// <summary>
    /// What the tool exits with.
    /// </summary>
    /// <remarks>
    /// Non-zero when anything is flagged. It is run by a person and by whatever
    /// they wire it into, and an exit code of nought with a report full of
    /// broken readers is a check that cannot fail — which is a check nobody
    /// acts on.
    /// </remarks>
    public static int ExitCode(IReadOnlyList<SourceHealthCheck> checks)
    {
        return checks.Any(one => one.Flagged) ? 1 : 0;
    }

    /// <summary>Reads what was written last time, or nothing at all.</summary>
    /// <remarks>
    /// An empty baseline for a file that is missing or unreadable, because the
    /// answer to both is the same: nothing is known about last time, so nothing
    /// is fewer than it. Failing to run over a file the tool itself wrote would
    /// be worse than the fault it is looking for.
    /// </remarks>
    public static HealthBaseline Read(string folder)
    {
        string path = Path.Combine(folder, FileName);

        try
        {
            return File.Exists(path)
                ? new(JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path))
                      ?? new Dictionary<string, int>())
                : new(new Dictionary<string, int>());
        }
        catch (Exception unreadable) when (unreadable is IOException or JsonException or UnauthorizedAccessException)
        {
            return new(new Dictionary<string, int>());
        }
    }

    /// <summary>Writes this run down for the next one to be judged against.</summary>
    public void Write(string folder)
    {
        Directory.CreateDirectory(folder);

        File.WriteAllText(
            Path.Combine(folder, FileName),
            JsonSerializer.Serialize(Rows, new JsonSerializerOptions { WriteIndented = true }));
    }
}

using System.Text;

namespace NoMercy.Plugin.TorrentDownloader.Tools.SourceHealth;

/// <summary>
/// What the walk found, written down: one report and the page each source
/// returned.
/// </summary>
/// <remarks>
/// The page is kept because repairing a reader is done by reading it. A report
/// saying "TorrentGalaxy read nothing" and throwing the page away leaves the
/// only evidence on a site that has since changed again.
/// </remarks>
public static class HealthReport
{
    public const string FileName = "report.md";

    /// <summary>
    /// Writes the report and the pages into <paramref name="folder"/> and
    /// answers where the report is.
    /// </summary>
    public static string Write(
        IReadOnlyList<SourceHealthCheck> checks,
        string folder,
        string term,
        DateTimeOffset when)
    {
        Directory.CreateDirectory(folder);

        // Keyed by position rather than by the check itself: a check is a
        // record, and two sources that answered the same thing are equal.
        Dictionary<int, string> pages = [];

        for (int index = 0; index < checks.Count; index++)
        {
            if (checks[index].Page is not string page)
            {
                continue;
            }

            // Named for the source it came from, and written before the report
            // that points at it — a link in the report to a file that is not
            // there is the same wrong answer as no report at all.
            string file = $"{Slug(checks[index].Source.Name)}.{Extension(page)}";
            File.WriteAllText(Path.Combine(folder, file), page);
            pages[index] = file;
        }

        StringBuilder report = new();

        report.AppendLine("# Source health");
        report.AppendLine();
        report.AppendLine(
            $"Asked for `{term}` on {when:yyyy-MM-dd HH:mm:ss} UTC. "
            + $"{checks.Count} sources walked, {checks.Count(check => check.Flagged)} flagged.");
        report.AppendLine();
        report.AppendLine("| Source | Condition | Rows | Releases on the page | Page |");
        report.AppendLine("| --- | --- | --- | --- | --- |");

        for (int index = 0; index < checks.Count; index++)
        {
            SourceHealthCheck check = checks[index];

            report.AppendLine(
                $"| {check.Source.Name} | {Words(check.Condition)} | {Number(check.Rows)} | {Number(check.Releases)} "
                + $"| {(pages.TryGetValue(index, out string? file) ? $"[{file}]({file})" : "no page")} |");
        }

        report.AppendLine();
        report.AppendLine("## What needs attention");
        report.AppendLine();

        SourceHealthCheck[] flagged = [.. checks.Where(check => check.Flagged)];

        if (flagged.Length == 0)
        {
            report.AppendLine("Nothing. Every source answered and every reader read what it answered.");
        }

        foreach (SourceHealthCheck check in flagged)
        {
            report.AppendLine($"### {check.Source.Name} — {Words(check.Condition)}");
            report.AppendLine();
            report.AppendLine(check.Detail);
            report.AppendLine();
            report.AppendLine($"Asked at `{check.Address}`{(check.Retried ? ", twice." : ".")}");
            report.AppendLine();
        }

        string path = Path.Combine(folder, FileName);
        File.WriteAllText(path, report.ToString());

        return path;
    }

    /// <summary>
    /// A count, or what is missing instead of one.
    /// </summary>
    /// <remarks>
    /// A source that was never read has no number of rows, and printing nought
    /// there says it was read and had none — which is a different source and a
    /// different job to do about it.
    /// </remarks>
    private static string Number(int? count)
    {
        return count?.ToString() ?? "not read";
    }

    private static string Words(SourceCondition condition)
    {
        return condition switch
        {
            SourceCondition.Answering => "answering",
            SourceCondition.NothingToSay => "nothing to offer",
            SourceCondition.NoRoute => "no route to a torrent",
            SourceCondition.BrokenReader => "reader saw nothing",
            SourceCondition.RateLimited => "rate-limited twice",
            SourceCondition.NoAnswer => "did not answer",
            _ => "no reader",
        };
    }

    /// <summary>Named for what it is, so nobody has to open it to find out.</summary>
    private static string Extension(string body)
    {
        string start = body.TrimStart();

        if (start.StartsWith('{') || start.StartsWith('['))
        {
            return "json";
        }

        return start.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
               || start.Contains("<rss", StringComparison.OrdinalIgnoreCase)
            ? "xml"
            : "html";
    }

    private static string Slug(string name)
    {
        return new string([.. name.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')]);
    }
}

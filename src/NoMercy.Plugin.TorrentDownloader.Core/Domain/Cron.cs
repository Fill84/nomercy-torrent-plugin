namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>
/// Checks a five-field cron expression, and says which field is wrong.
/// </summary>
/// <remarks>
/// Written here rather than taken from a library because Core references
/// nothing, and because the answer needed is not "can this be parsed" but "what
/// do I tell the owner". It does not compute the next occurrence: the server
/// owns the schedule, and a second implementation of that would be a second
/// answer to when a job runs.
///
/// It matters that this is checked at all. A cron the server cannot parse is
/// not refused at registration — the job is simply never scheduled — so the
/// owner is left with a plugin that looks configured and never runs.
/// </remarks>
public static class Cron
{
    private static readonly (string Name, int Minimum, int Maximum)[] Fields =
    [
        ("minute", 0, 59),
        ("hour", 0, 23),
        ("day of the month", 1, 31),
        ("month", 1, 12),
        ("day of the week", 0, 6),
    ];

    /// <summary>
    /// Whether <paramref name="expression"/> is a cron this plugin will accept,
    /// and when it is not, what to tell the owner.
    /// </summary>
    public static bool IsValid(string? expression, out string? reason)
    {
        string[] fields = (expression ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (fields.Length != 5)
        {
            reason = $"A cron has five fields — minute, hour, day of the month, month, day of the week — and this has {fields.Length}.";
            return false;
        }

        for (int index = 0; index < Fields.Length; index++)
        {
            if (!IsValidField(fields[index], Fields[index], out reason))
            {
                return false;
            }
        }

        reason = null;
        return true;
    }

    private static bool IsValidField(string field, (string Name, int Minimum, int Maximum) rules, out string? reason)
    {
        foreach (string part in field.Split(','))
        {
            if (!IsValidPart(part, rules, out reason))
            {
                return false;
            }
        }

        reason = null;
        return true;
    }

    private static bool IsValidPart(string part, (string Name, int Minimum, int Maximum) rules, out string? reason)
    {
        string range = part;

        int slash = part.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            range = part[..slash];
            string step = part[(slash + 1)..];

            // A step of nought never comes round, so it is a schedule that
            // silently never fires rather than one that fires often.
            if (!int.TryParse(step, out int every) || every < 1 || every > rules.Maximum)
            {
                reason = $"'{step}' is not a step for the {rules.Name} in '{part}'.";
                return false;
            }
        }

        if (range == "*")
        {
            reason = null;
            return true;
        }

        int dash = range.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0)
        {
            if (!Bound(range[..dash], rules, part, out int from, out reason)
                || !Bound(range[(dash + 1)..], rules, part, out int to, out reason))
            {
                return false;
            }

            if (from > to)
            {
                reason = $"'{range}' runs backwards for the {rules.Name}.";
                return false;
            }

            reason = null;
            return true;
        }

        return Bound(range, rules, part, out _, out reason);
    }

    private static bool Bound(
        string text,
        (string Name, int Minimum, int Maximum) rules,
        string part,
        out int value,
        out string? reason)
    {
        if (!int.TryParse(text, out value))
        {
            reason = $"'{text}' is not a {rules.Name} in '{part}'.";
            return false;
        }

        if (value < rules.Minimum || value > rules.Maximum)
        {
            reason = $"The {rules.Name} is {rules.Minimum} to {rules.Maximum}, and '{value}' is not.";
            return false;
        }

        reason = null;
        return true;
    }
}

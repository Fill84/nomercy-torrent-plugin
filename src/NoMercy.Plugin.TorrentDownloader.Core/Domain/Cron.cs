namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>
/// Checks a five-field cron expression, and says which field is wrong.
/// </summary>
/// <remarks>
/// Written here rather than taken from a library because Core references
/// nothing, and because the answer needed is not "can this be parsed" but "what
/// do I tell the owner".
///
/// It also says when a cadence next runs, because the owner asked for the
/// dashboard to show it and the server keeps that time to itself. The server
/// owns the schedule, so this is its answer and not a second one: NoMercyQueue
/// hands the expression to NCrontab with <c>DateTime.UtcNow</c> as the base,
/// and every expectation in its tests is what NCrontab 3.4.0 answered.
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

    /// <summary>The shortest interval between two cycles the owner accepts, in minutes.</summary>
    public const int ShortestIntervalMinutes = 15;

    /// <summary>
    /// Whether <paramref name="expression"/> never fires twice within fifteen minutes, and when it does,
    /// what to tell the owner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The owner's floor for a run, set on 15 September 2026</strong> in
    /// <c>docs/specs/release-names.md</c>: the shortest interval accepted is 15 minutes, and any longer
    /// one is accepted. It replaces the hourly floor of
    /// 13 September, which was set while every cycle still searched every indexer for every missing
    /// episode; a run now searches only the release names the feeds brought.
    /// </para>
    /// <para>
    /// <strong>Worked out from the minutes it fires, not from how it is written.</strong> Every minute of
    /// the day the minute and hour fields allow is listed, and each is compared with the next — the last
    /// of the day with the first of the next day included. A single step or a single number cannot
    /// answer this: <c>*/25</c> fires at 50 and again at 0, ten minutes later.
    /// </para>
    /// <para>
    /// <strong>The day fields are left out, which errs on the side of refusing.</strong> 23:58 and 00:03
    /// on a pattern limited to Sundays are a day apart, and this still counts them five minutes apart.
    /// Nothing the page offers is of that shape, and one typed by hand is refused with the reason.
    /// </para>
    /// </remarks>
    public static bool AtLeastFifteenMinutesApart(string? expression, out string? reason)
    {
        if (!IsValid(expression, out reason))
        {
            return false;
        }

        string[] fields = expression!.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool[] minutes = Allowed(fields[0], Fields[0]);
        bool[] hours = Allowed(fields[1], Fields[1]);

        List<int> fires = [];

        for (int hour = 0; hour < 24; hour++)
        {
            for (int minute = 0; minute < 60 && hours[hour]; minute++)
            {
                if (minutes[minute])
                {
                    fires.Add((hour * 60) + minute);
                }
            }
        }

        const int day = 24 * 60;
        int closest = day;

        for (int at = 0; at < fires.Count; at++)
        {
            int next = at + 1 < fires.Count ? fires[at + 1] : fires[0] + day;

            closest = Math.Min(closest, next - fires[at]);
        }

        if (closest >= ShortestIntervalMinutes)
        {
            reason = null;

            return true;
        }

        reason =
            $"'{expression}' would start a run {closest} minutes after the one before it, and the shortest "
            + $"interval is {ShortestIntervalMinutes} minutes. Use Run now for anything sooner.";

        return false;
    }

    /// <summary>
    /// When <paramref name="expression"/> next fires, strictly after
    /// <paramref name="after"/>, in UTC — or null for an expression the server
    /// could never schedule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In UTC because the server schedules in UTC. Strictly after, because a
    /// cadence asked at the minute it fires has just fired.
    /// </para>
    /// <para>
    /// A day of the month and a day of the week given together must both
    /// match. That is NCrontab's rule and not every cron's — a classic cron
    /// takes either — and NCrontab is what the server runs.
    /// </para>
    /// </remarks>
    public static DateTimeOffset? NextAfter(string? expression, DateTimeOffset after)
    {
        if (!IsValid(expression, out _))
        {
            return null;
        }

        string[] fields = expression!.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool[][] allowed = [.. Fields.Select((rules, at) => Allowed(fields[at], rules))];

        DateTime utc = after.UtcDateTime;
        DateTime start = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc)
            .AddMinutes(1);

        // Five years: every combination of day and month comes round inside
        // that, the twenty-ninth of February included.
        for (DateTime day = start.Date; day < start.Date.AddYears(5); day = day.AddDays(1))
        {
            if (!allowed[3][day.Month] || !allowed[2][day.Day] || !allowed[4][(int)day.DayOfWeek])
            {
                continue;
            }

            for (int hour = 0; hour < 24; hour++)
            {
                if (!allowed[1][hour])
                {
                    continue;
                }

                for (int minute = 0; minute < 60; minute++)
                {
                    if (!allowed[0][minute])
                    {
                        continue;
                    }

                    DateTime candidate = day.AddHours(hour).AddMinutes(minute);

                    if (candidate >= start)
                    {
                        return new DateTimeOffset(candidate, TimeSpan.Zero);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Which values one field lets through, indexed by value.</summary>
    private static bool[] Allowed(string field, (string Name, int Minimum, int Maximum) rules)
    {
        bool[] allowed = new bool[rules.Maximum + 1];

        foreach (string part in field.Split(','))
        {
            string range = part;
            int step = 1;

            int slash = part.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                range = part[..slash];
                step = int.Parse(part[(slash + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            }

            int from;
            int to;
            int dash = range.IndexOf('-', StringComparison.Ordinal);

            if (range == "*")
            {
                from = rules.Minimum;
                to = rules.Maximum;
            }
            else if (dash > 0)
            {
                from = int.Parse(range[..dash], System.Globalization.CultureInfo.InvariantCulture);
                to = int.Parse(range[(dash + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                from = int.Parse(range, System.Globalization.CultureInfo.InvariantCulture);

                // A single value with a step runs to the end of the field.
                to = slash >= 0 ? rules.Maximum : from;
            }

            for (int value = from; value <= to; value += step)
            {
                allowed[value] = true;
            }
        }

        return allowed;
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

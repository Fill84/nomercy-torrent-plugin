using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// The plugin's own log file: one a UTC day, under the plugin's data folder, the newest seven kept.
/// </summary>
/// <remarks>
/// <para>
/// It exists because the server lets no plugin line below Information through (<c>WebHostFactory.cs:37</c>),
/// so every Debug line the plugin ever wrote went nowhere, and GitHub issue #7 was an owner with a download
/// stuck at nought per cent and not one line about trackers, peers or pieces to read. The owner's choice of
/// 14 October 2026: Debug and Trace go to a file of the plugin's own.
/// </para>
/// <para>
/// <strong>A caller never waits for the disk.</strong> A line is queued and the caller goes on; whichever
/// thread finds nobody draining writes the queue out on the thread pool. The callers are peer
/// conversations and the piece picker at Trace, many times a second, and a log that made them wait for a
/// write would slow the very download it is describing.
/// </para>
/// <para>
/// <strong>And a caller is never thrown at.</strong> A full disk, a file the owner has open in an editor that
/// locks it, a folder that cannot be made: each costs lines of a log, and none of them may cost a staging or a
/// download, which is what an exception out of a log line would end.
/// </para>
/// </remarks>
public sealed class LogFile(string folder, TimeProvider time)
{
    /// <summary>How many days of files are kept: the newest seven, today's among them.</summary>
    public const int DaysKept = 7;

    /// <summary>What every file of this plugin's log is called before its date.</summary>
    private const string Prefix = "torrent-downloader-";

    /// <summary>
    /// How many lines may wait for the disk before the newest are dropped.
    /// </summary>
    /// <remarks>
    /// A disk that cannot keep up with Trace would otherwise turn the queue into a memory leak the size of
    /// every peer message a swarm sends. Dropped lines are counted and the file says how many went.
    /// </remarks>
    private const int MostWaiting = 20_000;

    private static readonly UTF8Encoding NoMark = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ConcurrentQueue<(DateOnly Day, string Text)> _waiting = new();

    /// <summary>One writer at a time, whether that is the drain or a <see cref="Flush"/>.</summary>
    private readonly Lock _writing = new();

    private int _count;
    private int _dropped;
    private int _draining;

    /// <summary>The last day a file was started on, so old files are cleared once a day and not on every write.</summary>
    private DateOnly? _started;

    /// <summary>Where the files are.</summary>
    public string Folder => folder;

    /// <summary>The file a given UTC day's lines go to.</summary>
    public static string NameFor(DateOnly day)
    {
        return $"{Prefix}{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log";
    }

    /// <summary>Queues one line, already formatted, and returns at once.</summary>
    public void Write(string level, string line)
    {
        DateTimeOffset now = time.GetUtcNow();

        if (Interlocked.Increment(ref _count) > MostWaiting)
        {
            Interlocked.Decrement(ref _count);
            Interlocked.Increment(ref _dropped);

            return;
        }

        string stamped = $"{now.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}Z [{level}] {line}";

        _waiting.Enqueue((DateOnly.FromDateTime(now.UtcDateTime), stamped));

        if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
        {
            ThreadPool.QueueUserWorkItem(static file => file.Drain(), this, preferLocal: false);
        }
    }

    /// <summary>Writes out everything queued so far, on the calling thread.</summary>
    /// <remarks>
    /// For a plugin being disposed, which should not leave its last lines in memory, and for a test, which
    /// has to read the file it is asserting on.
    /// </remarks>
    public void Flush()
    {
        lock (_writing)
        {
            WriteOut();
        }
    }

    /// <summary>Writes the queue out, and keeps going while lines go on arriving.</summary>
    /// <remarks>
    /// The flag is let go and the queue looked at once more: a line queued between the last write and the
    /// flag going would otherwise wait for the next line to be written at all.
    /// </remarks>
    private void Drain()
    {
        do
        {
            lock (_writing)
            {
                WriteOut();
            }

            Volatile.Write(ref _draining, 0);
        }
        while (!_waiting.IsEmpty && Interlocked.CompareExchange(ref _draining, 1, 0) == 0);
    }

    /// <summary>Everything queued, appended to its own day's file. Called under <see cref="_writing"/>.</summary>
    private void WriteOut()
    {
        if (_waiting.IsEmpty && Volatile.Read(ref _dropped) == 0)
        {
            return;
        }

        List<(DateOnly Day, string Text)> lines = [];

        while (_waiting.TryDequeue(out (DateOnly Day, string Text) line))
        {
            lines.Add(line);
        }

        Interlocked.Add(ref _count, -lines.Count);

        int dropped = Interlocked.Exchange(ref _dropped, 0);

        if (dropped > 0)
        {
            DateTimeOffset now = time.GetUtcNow();

            lines.Add((
                DateOnly.FromDateTime(now.UtcDateTime),
                $"{now.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}Z [Warning] "
                + $"{dropped} lines were not written: the file could not keep up."));
        }

        try
        {
            Directory.CreateDirectory(folder);

            foreach (IGrouping<DateOnly, (DateOnly Day, string Text)> day in lines.GroupBy(line => line.Day))
            {
                // Shared for reading and deleting, so the owner can follow the file while it grows and a
                // tool can rotate it away without this one refusing.
                using FileStream stream = new(
                    Path.Combine(folder, NameFor(day.Key)),
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                using StreamWriter writer = new(stream, NoMark);

                foreach ((DateOnly _, string text) in day)
                {
                    writer.WriteLine(text);
                }

                if (_started != day.Key)
                {
                    _started = day.Key;
                    ClearOld();
                }
            }
        }
        catch (Exception)
        {
            // Every exception, not a list of them: this runs on the thread pool, where one that escapes ends
            // the server's process, and the lines it loses are worth less than any of what that would end.
        }
    }

    /// <summary>Deletes every day's file but the newest <see cref="DaysKept"/>.</summary>
    /// <remarks>
    /// Only files named exactly as this class names them, with a date that reads as one. Anything else in
    /// the folder — a copy the owner made, a note — was not made here and is not this plugin's to delete.
    /// </remarks>
    private void ClearOld()
    {
        DateOnly[] days =
        [
            .. Directory.EnumerateFiles(folder, $"{Prefix}*.log")
                .Select(Path.GetFileNameWithoutExtension)
                .Select(name => DateOnly.TryParseExact(
                    name![Prefix.Length..],
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly day)
                    ? day
                    : (DateOnly?)null)
                .OfType<DateOnly>()
                .OrderDescending(),
        ];

        foreach (DateOnly old in days.Skip(DaysKept))
        {
            try
            {
                File.Delete(Path.Combine(folder, NameFor(old)));
            }
            catch (Exception held) when (held is IOException or UnauthorizedAccessException)
            {
                // Open somewhere else; it goes on a later day.
            }
        }
    }
}

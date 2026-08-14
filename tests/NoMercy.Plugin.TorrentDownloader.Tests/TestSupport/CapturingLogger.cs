using Microsoft.Extensions.Logging;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// An <see cref="ILogger"/> that keeps what it was told, so a test can assert
/// on what the owner would read in the server's log.
/// </summary>
/// <remarks>
/// It stores the formatted line rather than the template and its arguments,
/// because the line is what a person sees. A test asserting on the template
/// would pass while the message rendered as "Torrent Downloader {Version}".
/// </remarks>
public sealed class CapturingLogger : ILogger
{
    private readonly List<(LogLevel Level, string Line)> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines.Select(entry => entry.Line)];
            }
        }
    }

    /// <summary>
    /// What was said, and how loudly.
    /// </summary>
    /// <remarks>
    /// The level is part of whether something was said at all: a broken
    /// catalogue reported at trace level is a broken catalogue nobody hears
    /// about, which is the same failure as not reporting it.
    /// </remarks>
    public IReadOnlyList<(LogLevel Level, string Line)> Entries
    {
        get
        {
            lock (_lines)
            {
                return _lines.ToArray();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_lines)
        {
            _lines.Add((logLevel, formatter(state, exception)));
        }
    }
}

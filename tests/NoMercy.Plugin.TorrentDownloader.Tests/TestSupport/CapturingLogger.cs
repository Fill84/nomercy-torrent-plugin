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
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
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
            _lines.Add(formatter(state, exception));
        }
    }
}

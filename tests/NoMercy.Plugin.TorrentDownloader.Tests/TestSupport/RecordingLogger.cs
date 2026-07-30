// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using Microsoft.Extensions.Logging;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

// A settable double for ILogger that keeps the formatted text of every entry. Used where a
// test needs to prove distinct work happened - four scheduled jobs each logging a different
// sentence - without reaching into the plugin's private members to do it.
public sealed class RecordingLogger : ILogger
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
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
        Func<TState, Exception?, string> formatter
    )
    {
        Messages.Add(formatter(state, exception));
    }
}

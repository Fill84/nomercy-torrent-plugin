using NoMercy.Plugin.TorrentDownloader.Core.Ports;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

/// <summary>The ledger, as a test can read it back.</summary>
/// <remarks>
/// A list rather than a database: what is being asserted is that a stage writes
/// down what a site answered, and the store that keeps it is tested elsewhere.
/// </remarks>
public sealed class RecordingLedger : ISourceLedger
{
    private readonly Lock _lock = new();
    private readonly List<SourceAnswer> _answers = [];

    /// <summary>Every ask, in the order they were written down.</summary>
    public IReadOnlyList<SourceAnswer> Answers
    {
        get
        {
            lock (_lock)
            {
                return [.. _answers];
            }
        }
    }

    public Task RecordAsync(SourceAnswer answer, CancellationToken ct)
    {
        // Every indexer is asked at once, so this is written to from several
        // threads at a time and a bare list would lose entries.
        lock (_lock)
        {
            _answers.Add(answer);
        }

        return Task.CompletedTask;
    }
}

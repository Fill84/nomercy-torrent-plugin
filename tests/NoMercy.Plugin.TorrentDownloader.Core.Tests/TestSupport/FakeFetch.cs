using NoMercy.Plugin.TorrentDownloader.Core.Sources;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

/// <summary>
/// A fetch that answers from a script, records what it was asked, and can be
/// held open until the test lets it go.
/// </summary>
/// <remarks>
/// The holding is what proves the harvest reads its feeds together: every one
/// of them has to be in flight at the same moment, and only the thing being
/// fetched can say whether they were.
/// </remarks>
public sealed class FakeFetch(TimeProvider? time = null) : IFetch
{
    private readonly Dictionary<string, (string? Body, FetchFailure? Failure, TimeSpan Takes)> _answers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _lock = new();
    private readonly List<Uri> _asked = [];
    private readonly TaskCompletionSource _allInFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _inFlight;

    /// <summary>Every address it was asked for, in the order they arrived.</summary>
    public IReadOnlyList<Uri> Asked
    {
        get
        {
            lock (_lock)
            {
                return [.. _asked];
            }
        }
    }

    /// <summary>How many answers are being waited on right now.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>How many were scripted, which is how many should be in flight at once.</summary>
    public int Expected => _answers.Count;

    /// <summary>Completes when every scripted address is being waited on at the same moment.</summary>
    public Task AllInFlight => _allInFlight.Task;

    public FakeFetch Answers(string address, string body, TimeSpan? takes = null)
    {
        _answers[address] = (body, null, takes ?? TimeSpan.Zero);

        return this;
    }

    public FakeFetch Fails(string address, FetchOutcome outcome, string reason)
    {
        _answers[address] = (null, FetchFailure.For(outcome, new(address), reason), TimeSpan.Zero);

        return this;
    }

    /// <summary>
    /// An address that throws rather than answering.
    /// </summary>
    /// <remarks>
    /// Not the same as failing: a failure is a fetch saying no, and this is
    /// something nobody planned for. A stage that fans out has to survive both.
    /// </remarks>
    public FakeFetch Throws(string address, Exception exception)
    {
        _throws[address] = exception;
        _answers[address] = (null, null, TimeSpan.Zero);

        return this;
    }

    private readonly Dictionary<string, Exception> _throws = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One body for every address that was not scripted by name.
    /// </summary>
    /// <remarks>
    /// For the tests about <em>what was asked</em> rather than what came back:
    /// a stage that works out its own query terms cannot have every address it
    /// will build written down in advance without the test asserting the answer
    /// twice.
    /// </remarks>
    public FakeFetch AnswersAnything(string body)
    {
        _anything = body;

        return this;
    }

    /// <summary>Every address on this host fails, whatever it is.</summary>
    public FakeFetch FailsHost(string host, FetchOutcome outcome, string reason)
    {
        _failedHosts[host] = (outcome, reason);

        return this;
    }

    private string? _anything;
    private readonly Dictionary<string, (FetchOutcome Outcome, string Reason)> _failedHosts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether this address is one the test scripted.</summary>
    public bool Knows(string address)
    {
        return _answers.ContainsKey(address);
    }

    public async Task<FetchResult> GetAsync(Uri address, bool gated, CancellationToken ct)
    {
        lock (_lock)
        {
            _asked.Add(address);
        }

        if (_failedHosts.TryGetValue(address.Host, out (FetchOutcome Outcome, string Reason) refused))
        {
            return FetchResult.Failed(FetchFailure.For(refused.Outcome, address, refused.Reason));
        }

        if (!_answers.TryGetValue(address.ToString(), out (string? Body, FetchFailure? Failure, TimeSpan Takes) answer))
        {
            answer = _anything is not null
                ? (_anything, null, TimeSpan.Zero)
                : throw new InvalidOperationException($"Nothing scripted for {address}.");
        }

        if (_throws.TryGetValue(address.ToString(), out Exception? thrown))
        {
            throw thrown;
        }

        if (Interlocked.Increment(ref _inFlight) == Expected)
        {
            _allInFlight.TrySetResult();
        }

        try
        {
            if (answer.Takes > TimeSpan.Zero)
            {
                // On the test's clock, so the test decides when this answers.
                await Task.Delay(answer.Takes, time ?? TimeProvider.System, ct);
            }

            return answer.Body is not null
                ? FetchResult.Fetched(answer.Body)
                : FetchResult.Failed(answer.Failure!);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

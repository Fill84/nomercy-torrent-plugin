using Microsoft.Extensions.Logging;
using NoMercy.Events;
using NoMercy.Events.Encoding;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// Listens to the server's own encoding events and remembers what it heard.
/// </summary>
/// <remarks>
/// <para>
/// The media server has published <see cref="EncodingStartedEvent"/>,
/// <see cref="EncodingCompletedEvent"/> and <see cref="EncodingFailedEvent"/>
/// all along, and this plugin asked instead — once per job per grab per
/// transfers tick. Nothing had to.
/// </para>
/// <para>
/// Held in memory and not written down. A restart loses it, and that is
/// correct: what a restart then reads is null, which means "nothing has been
/// said" rather than "finished", and the library decides — exactly as it did
/// for a job the server no longer knew.
/// </para>
/// </remarks>
public sealed class EncoderSays : IEncoderSays, IDisposable
{
    /// <summary>How many encodes are remembered at once.</summary>
    /// <remarks>
    /// A server that runs for months encodes thousands of files and this is all
    /// in memory. What a grab can still be waiting on is among the most recent,
    /// so the oldest are forgotten; forgetting one costs nothing, because the
    /// library is the stronger proof and is what answers when nothing has been
    /// said.
    /// </remarks>
    public const int Most = 512;

    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<int, Word> _said = [];
    private readonly List<IDisposable> _listening = [];

    /// <summary>Which media row the server has just said something about.</summary>
    /// <remarks>
    /// What sets the rest of the chain going. Staging, deleting the download
    /// and marking the grab done all waited for the transfers cadence to come
    /// round; the whole point of hearing the encoder is that they happen when
    /// the encode really ended.
    /// </remarks>
    public event Action<int>? Said;

    public EncoderSays(IEventBus bus, ILogger logger, TimeProvider? time = null)
    {
        _logger = logger;
        _time = time ?? TimeProvider.System;

        _listening.Add(bus.Subscribe<EncodingStartedEvent>((started, _) =>
        {
            // A file the server is still reading. A download taken away under
            // one of these is the fault that cost the owner 36 GB on
            // 31 August 2026.
            Heard(started.JobId, new(EncodeJobState.Running, null));

            return Task.CompletedTask;
        }));

        _listening.Add(bus.Subscribe<EncodingCompletedEvent>((done, _) =>
        {
            Heard(done.JobId, new(EncodeJobState.Finished, null));

            return Task.CompletedTask;
        }));

        _listening.Add(bus.Subscribe<EncodingFailedEvent>((wrong, _) =>
        {
            // The reason, because it is what the owner reads on the History
            // page. Without it a grab is failed with "the server gave up and
            // said no more than that".
            Heard(wrong.JobId, new(EncodeJobState.Failed, wrong.ErrorMessage));

            return Task.CompletedTask;
        }));
    }

    public EncodeJob? About(int mediaId)
    {
        lock (_lock)
        {
            return _said.TryGetValue(mediaId, out Word word) ? word.Job : null;
        }
    }

    public void Dispose()
    {
        foreach (IDisposable one in _listening)
        {
            one.Dispose();
        }

        _listening.Clear();
    }

    /// <summary>Writes down what was said, and tells whoever is waiting on it.</summary>
    private void Heard(int mediaId, EncodeJob job)
    {
        lock (_lock)
        {
            _said[mediaId] = new(job, _time.GetUtcNow());

            Forget();
        }

        // Outside the lock. A handler runs a whole transfers pass — staging,
        // deleting downloads, asking the server for things — and none of that
        // has any business holding this.
        Told(mediaId);
    }

    /// <summary>Drops the oldest once there are too many. Called under the lock.</summary>
    private void Forget()
    {
        if (_said.Count <= Most)
        {
            return;
        }

        foreach (int old in _said
                     .OrderBy(one => one.Value.At)
                     .Take(_said.Count - Most)
                     .Select(one => one.Key)
                     .ToArray())
        {
            _said.Remove(old);
        }
    }

    /// <summary>Raises <see cref="Said"/> without letting a handler take the server down.</summary>
    /// <remarks>
    /// This runs on the server's own event bus, which publishes to every
    /// subscriber in turn: an exception escaping here is this plugin stopping
    /// the rest of the server hearing about its own encodes.
    /// </remarks>
    private void Told(int mediaId)
    {
        try
        {
            Said?.Invoke(mediaId);
        }
        catch (Exception wrong)
        {
            _logger.LogWarning(
                wrong,
                "The server said something about encode {Media} and acting on it went wrong: {Reason}",
                mediaId,
                wrong.Message);
        }
    }

    /// <summary>One thing said, and when.</summary>
    private readonly record struct Word(EncodeJob Job, DateTimeOffset At);
}

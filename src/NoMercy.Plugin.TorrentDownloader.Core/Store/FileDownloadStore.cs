// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoMercy.Plugin.TorrentDownloader.Core.Store;

/// <summary>
/// The store, as one file.
///
/// <para>
/// SQLite was the first choice until it turned out the host does not share
/// <c>Microsoft.Data.Sqlite</c> with plugins: using it would mean shipping a second
/// native SQLite alongside the media server's own, in the same process. That is not a
/// risk worth taking with somebody's media server for a few thousand rows.
/// </para>
///
/// <para>
/// So: the whole state is held in memory and written atomically on change, the same
/// pattern <c>FileResumeStore</c> already runs. At this size an index in memory beats
/// a query planner, and the durability story is one anyone can reason about - the file
/// on disk is either the previous state or the new one, never half of either.
/// </para>
/// </summary>
public sealed class FileDownloadStore(string path) : IDownloadStore
{
    /// <summary>How much history is kept. See <see cref="RecordHistoryAsync"/> for why there is a bound at all.</summary>
    private const int MaxHistoryEntries = 500;

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private State? _state;

    public async Task RefreshWantedAsync(IReadOnlyList<WantedEpisode> missing, CancellationToken ct) =>
        await MutateAsync(state =>
        {
            // Indexed by hand rather than with ToDictionary, which throws on a repeated
            // key. It threw on a real server: an older build had written the same episode
            // twice, so every refresh after that died reading the file back and the queue
            // froze at whatever it held when the duplicate landed. A store that cannot
            // read its own state has no way out except deleting the file.
            Dictionary<EpisodeKey, WantedEpisode> known = [];

            foreach (WantedEpisode episode in state.Wanted)
                known[episode.Key] = episode;

            // DistinctBy on the way in, so the file stops being able to reach that state
            // at all. The wanted list is a set keyed by episode; two rows for one slot is
            // not a smaller problem than failing to read them.
            state.Wanted = [.. missing.DistinctBy(episode => episode.Key).Select(episode =>
                known.TryGetValue(episode.Key, out WantedEpisode? seen)
                    // Keep what was learned. Resetting the attempt count every refresh
                    // restarts the back-off, and the plugin hammers a dead release forever.
                    ? episode with { State = seen.State, LastSearchedAt = seen.LastSearchedAt, SearchAttempts = seen.SearchAttempts }
                    : episode)];
        }, ct);

    public async Task<IReadOnlyList<WantedEpisode>> WantedAsync(int limit, CancellationToken ct) =>
        await ReadAsync<IReadOnlyList<WantedEpisode>>(state =>
        [
            .. state.Wanted
                .Where(episode => episode.State is WantedState.Wanted or WantedState.Searching)
                // Least recently searched first. With a backlog of hundreds any other
                // order means the same few rows are looked at every cycle.
                .OrderBy(episode => episode.LastSearchedAt ?? DateTimeOffset.MinValue)
                .ThenBy(episode => episode.Key.ShowId)
                .ThenBy(episode => episode.Key.Season)
                .ThenBy(episode => episode.Key.Episode)
                .Take(limit),
        ], ct);

    public async Task RecordShowsAsync(IReadOnlyList<TrackedShow> shows, CancellationToken ct) =>
        await MutateAsync(state => state.Shows = [.. shows.DistinctBy(show => show.ShowId)], ct);

    public async Task<IReadOnlyList<TrackedShow>> ShowsAsync(CancellationToken ct) =>
        await ReadAsync(state => (IReadOnlyList<TrackedShow>)[.. state.Shows], ct);

    public async Task<WantedEpisode?> FindWantedAsync(EpisodeKey key, CancellationToken ct) =>
        await ReadAsync(state => state.Wanted.FirstOrDefault(episode => episode.Key == key), ct);

    public async Task MarkSearchedAsync(EpisodeKey key, DateTimeOffset when, WantedState wanted, CancellationToken ct) =>
        await MutateAsync(state =>
        {
            int index = state.Wanted.FindIndex(episode => episode.Key == key);

            if (index >= 0)
            {
                state.Wanted[index] = state.Wanted[index] with
                {
                    State = wanted,
                    LastSearchedAt = when,
                    SearchAttempts = state.Wanted[index].SearchAttempts + 1,
                };
            }
        }, ct);

    public async Task AddGrabAsync(Grab grab, CancellationToken ct) =>
        await MutateAsync(state =>
        {
            state.Grabs.RemoveAll(existing => existing.InfoHash == grab.InfoHash);
            state.Grabs.Add(grab);
        }, ct);

    public async Task<Grab?> FindGrabAsync(string infoHash, CancellationToken ct) =>
        await ReadAsync(state => state.Grabs.FirstOrDefault(grab => grab.InfoHash == infoHash), ct);

    public async Task<IReadOnlyList<Grab>> ActiveGrabsAsync(CancellationToken ct) =>
        await ReadAsync<IReadOnlyList<Grab>>(state =>
        [
            // Resolving belongs here, and leaving it out was not cosmetic: the search cycle
            // works out how much room it has from this set, so five downloads waiting on
            // their swarm counted as zero and the next cycle started five more - no ceiling
            // at all. The pages also read this to find out what a transfer is called, so
            // without it the owner saw a raw info hash.
            .. state.Grabs.Where(grab => grab.State
                is GrabState.Grabbed
                or GrabState.Resolving
                or GrabState.Downloading
                or GrabState.Paused
                or GrabState.Downloaded),
        ], ct);

    public async Task UpdateGrabAsync(
        string infoHash,
        GrabState grabState,
        string? failureReason,
        DateTimeOffset? finishedAt,
        CancellationToken ct) =>
        await MutateAsync(state =>
        {
            int index = state.Grabs.FindIndex(grab => grab.InfoHash == infoHash);

            if (index >= 0)
                state.Grabs[index] = state.Grabs[index] with { State = grabState, FailureReason = failureReason, FinishedAt = finishedAt };
        }, ct);

    public async Task RecordCompletedPathAsync(string infoHash, string completedPath, CancellationToken ct) =>
        await MutateAsync(state =>
        {
            int index = state.Grabs.FindIndex(grab => grab.InfoHash == infoHash);

            if (index >= 0)
                state.Grabs[index] = state.Grabs[index] with { CompletedPath = completedPath };
        }, ct);

    public async Task RecordTransferAsync(Transfer transfer, CancellationToken ct) =>
        await MutateAsync(state =>
        {
            state.Transfers.RemoveAll(existing => existing.InfoHash == transfer.InfoHash);
            state.Transfers.Add(transfer);
        }, ct);

    public async Task<IReadOnlyList<Transfer>> TransfersAsync(CancellationToken ct) =>
        await ReadAsync(state => (IReadOnlyList<Transfer>)[.. state.Transfers], ct);

    /// <summary>
    /// Appends to the history, oldest trimmed away.
    ///
    /// <para>
    /// Capped because this store is one JSON file rewritten in full on every change, and a
    /// history nobody prunes turns every later write into a slower one. Five hundred is
    /// months of an ordinary server and still a file that saves instantly.
    /// </para>
    /// </summary>
    public async Task RecordHistoryAsync(HistoryEntry entry, CancellationToken ct) =>
        await MutateAsync(state =>
        {
            state.History.Add(entry);

            if (state.History.Count > MaxHistoryEntries)
                state.History.RemoveRange(0, state.History.Count - MaxHistoryEntries);
        }, ct);

    public async Task<IReadOnlyList<HistoryEntry>> HistoryAsync(int limit, CancellationToken ct) =>
        await ReadAsync(state => (IReadOnlyList<HistoryEntry>)
        [
            .. state.History
                .OrderByDescending(entry => entry.At)
                .Take(Math.Max(0, limit)),
        ], ct);

    public async Task BlacklistAsync(BlacklistEntry entry, CancellationToken ct) =>
        await MutateAsync(state => state.Blacklist.Add(entry), ct);

    public async Task<IReadOnlyList<BlacklistEntry>> BlacklistedAsync(DateTimeOffset now, CancellationToken ct) =>
        await ReadAsync(state => (IReadOnlyList<BlacklistEntry>)
        [
            .. state.Blacklist
                .Where(entry => entry.ExpiresAt is null || entry.ExpiresAt > now)
                .OrderByDescending(entry => entry.AddedAt),
        ], ct);

    public async Task<bool> AllowAgainAsync(string handle, CancellationToken ct)
    {
        bool lifted = false;

        await MutateAsync(state =>
        {
            lifted = state.Blacklist.RemoveAll(entry =>
                string.Equals(entry.Handle, handle, StringComparison.OrdinalIgnoreCase)) > 0;
        }, ct);

        return lifted;
    }

    public async Task<bool> IsBlacklistedAsync(string? infoHash, string releaseTitle, DateTimeOffset now, CancellationToken ct) =>
        await ReadAsync(state => state.Blacklist.Any(entry =>
            (entry.ExpiresAt is null || entry.ExpiresAt > now)
            && ((infoHash is not null && string.Equals(entry.InfoHash, infoHash, StringComparison.OrdinalIgnoreCase))
                || string.Equals(entry.ReleaseTitle, releaseTitle, StringComparison.OrdinalIgnoreCase))), ct);

    private async Task<T> ReadAsync<T>(Func<State, T> read, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);

        try
        {
            return read(await LoadAsync(ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task MutateAsync(Action<State> mutate, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);

        try
        {
            State state = await LoadAsync(ct);
            mutate(state);
            await SaveAsync(state, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<State> LoadAsync(CancellationToken ct)
    {
        if (_state is not null)
            return _state;

        if (!File.Exists(path))
            return _state = new State();

        try
        {
            await using FileStream stream = File.OpenRead(path);
            _state = await JsonSerializer.DeserializeAsync<State>(stream, Format, ct) ?? new State();
        }
        catch (Exception failure) when (failure is IOException or JsonException)
        {
            // Unreadable means start over. This file holds history and in-flight state,
            // not the user's media: losing it costs a re-search, not a re-download of
            // anything already imported.
            _state = new State();
        }

        return _state;
    }

    private async Task SaveAsync(State state, CancellationToken ct)
    {
        string? folder = Path.GetDirectoryName(path);

        if (folder is not null)
            Directory.CreateDirectory(folder);

        // Write beside it and move it into place, so a crash partway through leaves the
        // previous state rather than a truncated file that reads as an empty database.
        string temporary = path + ".writing";

        await using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, state, Format, ct);
            await stream.FlushAsync(ct);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Everything the plugin remembers, as one serialisable object.</summary>
    private sealed class State
    {
        // 2: shows the plugin is not working on stopped being recorded at all, so the
        // separate UnstartedShows list went with them. Nothing migrates - every list here
        // is what the last refresh concluded, and the next one rewrites it. A version 1
        // file simply carries a property this class no longer has, which the deserialiser
        // skips.
        public int Version { get; set; } = 2;

        public List<WantedEpisode> Wanted { get; set; } = [];

        // Added after the store already existed. An older file has none until the next
        // refresh writes them, which is fine: this is what the last refresh concluded, not
        // something only recorded once.
        public List<TrackedShow> Shows { get; set; } = [];

        public List<Grab> Grabs { get; set; } = [];

        public List<Transfer> Transfers { get; set; } = [];

        public List<BlacklistEntry> Blacklist { get; set; } = [];

        public List<HistoryEntry> History { get; set; } = [];
    }
}

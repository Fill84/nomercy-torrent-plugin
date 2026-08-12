// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Store;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

/// <summary>
/// The store, without a database.
///
/// <para>
/// Every orchestrator test uses this. Deciding what to download is not a question about
/// SQL, and a test that needs a file to prove a decision is a test that will one day
/// fail for a reason that has nothing to do with the decision.
/// </para>
///
/// <para>
/// It is held to the same behaviour as the real one: the shared suite in
/// <c>DownloadStoreContract</c> runs against both.
/// </para>
/// </summary>
public sealed class InMemoryDownloadStore : IDownloadStore
{
    private readonly Dictionary<EpisodeKey, WantedEpisode> _wanted = [];
    private readonly Dictionary<string, Grab> _grabs = [];
    private readonly Dictionary<string, Transfer> _transfers = [];
    private readonly List<BlacklistEntry> _blacklist = [];

    public Task RefreshWantedAsync(IReadOnlyList<WantedEpisode> missing, CancellationToken ct)
    {
        HashSet<EpisodeKey> stillMissing = [.. missing.Select(episode => episode.Key)];

        foreach (EpisodeKey key in _wanted.Keys.Where(key => !stillMissing.Contains(key)).ToList())
            _wanted.Remove(key);

        foreach (WantedEpisode episode in missing)
        {
            // Keep what we learned about an episode we already knew about: how often it
            // was searched and when. Overwriting would restart the back-off every refresh.
            _wanted[episode.Key] = _wanted.TryGetValue(episode.Key, out WantedEpisode? known)
                ? episode with
                {
                    State = known.State,
                    LastSearchedAt = known.LastSearchedAt,
                    SearchAttempts = known.SearchAttempts,
                }
                : episode;
        }

        return Task.CompletedTask;
    }

    private List<TrackedShow> _shows = [];

    public Task RecordShowsAsync(IReadOnlyList<TrackedShow> shows, CancellationToken ct)
    {
        _shows = [.. shows.DistinctBy(show => show.ShowId)];
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TrackedShow>> ShowsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TrackedShow>>([.. _shows]);

    public Task<IReadOnlyList<WantedEpisode>> WantedAsync(int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WantedEpisode>>(
        [
            .. _wanted.Values
                .Where(episode => episode.State is WantedState.Wanted or WantedState.Searching)
                .OrderBy(episode => episode.LastSearchedAt ?? DateTimeOffset.MinValue)
                .ThenBy(episode => episode.Key.ShowId)
                .ThenBy(episode => episode.Key.Season)
                .ThenBy(episode => episode.Key.Episode)
                .Take(limit),
        ]);

    public Task<WantedEpisode?> FindWantedAsync(EpisodeKey key, CancellationToken ct) =>
        Task.FromResult(_wanted.TryGetValue(key, out WantedEpisode? episode) ? episode : null);

    public Task MarkSearchedAsync(EpisodeKey key, DateTimeOffset when, WantedState state, CancellationToken ct)
    {
        if (_wanted.TryGetValue(key, out WantedEpisode? episode))
        {
            _wanted[key] = episode with
            {
                State = state,
                LastSearchedAt = when,
                SearchAttempts = episode.SearchAttempts + 1,
            };
        }

        return Task.CompletedTask;
    }

    public Task AddGrabAsync(Grab grab, CancellationToken ct)
    {
        _grabs[grab.InfoHash] = grab;
        return Task.CompletedTask;
    }

    public Task<Grab?> FindGrabAsync(string infoHash, CancellationToken ct) =>
        Task.FromResult(_grabs.TryGetValue(infoHash, out Grab? grab) ? grab : null);

    public Task<IReadOnlyList<Grab>> ActiveGrabsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Grab>>(
        [
            // Resolving counts, exactly as it does in the real store. A double that
            // disagrees about which downloads are running is a double that hides the bug
            // where the limit stops working.
            .. _grabs.Values.Where(grab => grab.State
                is GrabState.Grabbed
                or GrabState.Resolving
                or GrabState.Downloading
                or GrabState.Paused
                or GrabState.Downloaded),
        ]);

    public Task UpdateGrabAsync(
        string infoHash,
        GrabState state,
        string? failureReason,
        DateTimeOffset? finishedAt,
        CancellationToken ct)
    {
        if (_grabs.TryGetValue(infoHash, out Grab? grab))
            _grabs[infoHash] = grab with { State = state, FailureReason = failureReason, FinishedAt = finishedAt };

        return Task.CompletedTask;
    }

    // Faithful to the real store, because the last two bugs this double could have caught
    // were both a field it quietly did not carry.
    public Task RecordCompletedPathAsync(string infoHash, string completedPath, CancellationToken ct)
    {
        if (_grabs.TryGetValue(infoHash, out Grab? grab))
            _grabs[infoHash] = grab with { CompletedPath = completedPath };

        return Task.CompletedTask;
    }

    private readonly Dictionary<string, SourceReport> _sources = [];

    public Task RecordSourceReportsAsync(IReadOnlyList<SourceReport> reports, CancellationToken ct)
    {
        foreach (SourceReport report in reports)
            _sources[report.Name] = report;

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SourceReport>> SourceReportsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SourceReport>>([.. _sources.Values]);

    public Task RecordTransferAsync(Transfer transfer, CancellationToken ct)
    {
        _transfers[transfer.InfoHash] = transfer;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Transfer>> TransfersAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Transfer>>([.. _transfers.Values]);

    public List<HistoryEntry> History { get; } = [];

    public Task RecordHistoryAsync(HistoryEntry entry, CancellationToken ct)
    {
        History.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<HistoryEntry>> HistoryAsync(int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<HistoryEntry>>(
            [.. History.OrderByDescending(entry => entry.At).Take(Math.Max(0, limit))]);

    public Task<IReadOnlyList<BlacklistEntry>> BlacklistedAsync(DateTimeOffset now, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BlacklistEntry>>(
            [.. _blacklist.Where(entry => entry.ExpiresAt is null || entry.ExpiresAt > now)]);

    public Task<bool> AllowAgainAsync(string handle, CancellationToken ct) =>
        Task.FromResult(_blacklist.RemoveAll(entry =>
            string.Equals(entry.Handle, handle, StringComparison.OrdinalIgnoreCase)) > 0);

    public Task BlacklistAsync(BlacklistEntry entry, CancellationToken ct)
    {
        _blacklist.Add(entry);
        return Task.CompletedTask;
    }

    public Task<bool> IsBlacklistedAsync(string? infoHash, string releaseTitle, DateTimeOffset now, CancellationToken ct) =>
        Task.FromResult(_blacklist.Any(entry =>
            (entry.ExpiresAt is null || entry.ExpiresAt > now)
            && ((infoHash is not null && string.Equals(entry.InfoHash, infoHash, StringComparison.OrdinalIgnoreCase))
                || string.Equals(entry.ReleaseTitle, releaseTitle, StringComparison.OrdinalIgnoreCase))));
}

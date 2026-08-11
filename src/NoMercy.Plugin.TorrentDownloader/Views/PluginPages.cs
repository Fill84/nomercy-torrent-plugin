// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Adapters;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Library;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// Every page this plugin serves, and the reading each one needs to draw itself.
///
/// <para>
/// The plugin class implements three contracts - a plugin, a set of scheduled jobs, and a
/// user interface - and the third had grown to a third of the file. Being the thing the
/// host loads and being the thing that renders eleven pages are different jobs, and only
/// one of them changes when a column moves on the downloads table.
/// </para>
///
/// <para>
/// Each page loads only what it shows. The single page this replaced read the whole store
/// on every render because it drew the whole store; a queue that no longer carries history
/// has no reason to read it.
/// </para>
///
/// <para>
/// Nothing here throws at the caller. These pages are the plugin's only diagnostic surface,
/// so a load failure that escapes shows the owner a broken page instead of telling them
/// their key ring rotated or their config is truncated.
/// </para>
/// </summary>
internal sealed class PluginPages(
    IPluginContext context,
    SettingsGateway settings,
    Func<CancellationToken, Task<IDownloadStore>> openStore,
    IClock clock)
{

    // These pages are the plugin's only diagnostic surface, so letting a load failure throw
    // through one hides its own cause - the owner sees a broken page instead of learning
    // their key ring rotated or their config file is truncated. Secret reads go through the
    // host's data protector, which throws CryptographicException on a rotated key ring or a
    // corrupt payload; a truncated config throws JsonException the same way Fix 1 guards
    // against on the registration path. Rendered text and the log message both name what
    // failed, never the exception detail, the settings, or a stored secret - which is also
    // why one card serves both routes: what went wrong is in the log, and guessing at it in
    // the card is how a downloads page ends up advising someone about an encryption key.
    private static PluginView PageErrorView() =>
        PluginViews.Declarative(
            Ui.Container(
                "page-error",
                Ui.Badge("page-error-badge", "Unavailable", PluginBadgeVariant.Danger),
                Ui.EmptyState(
                    "page-error-empty",
                    "This page could not be loaded",
                    "Check the server log for Torrent Downloader - it names what failed."
                )
            )
        );

    // Resolved through the plugin's own route table rather than matched as strings, so the
    // set of pages the server lists and the set this answers cannot disagree. A client asking
    // for anything else is not a bug worth failing the request over - the empty state is the
    // honest answer for a route this version does not have.
    //
    // Each page loads only what it shows. The old single page read the whole store on every
    // render because it drew the whole store; a queue that no longer carries history has no
    // reason to read it.
    public async Task<PluginView> BuildAsync(PluginViewRequest request, CancellationToken ct)
    {

        try
        {
            PluginRouteMatch? match = Pages.Routes.Resolve(request.Route);

            PluginView view = match?.Route.Name switch
            {
                Pages.Source => await SourcePageAsync(context, match.Param("index"), ct),
                Pages.Shows => ShowsView.Build(await ShowsAsync(context, ct)),
                Pages.Show => await ShowPageAsync(context, match.Param("showId"), ct),
                Pages.Overview => await OverviewPageAsync(context, ct),
                Pages.Downloads => await DownloadsPageAsync(context, ct),
                Pages.Download => await DownloadPageAsync(context, match.Param("infoHash"), ct),
                Pages.Queue => QueueView.Build(await WantedAsync(context, ct)),
                Pages.History => HistoryView.Build(await HistoryAsync(context, HistoryView.Limit, ct)),
                Pages.Sources => await SourcesPageAsync(context, ct),
                Pages.Skipped => await SkippedPageAsync(context, ct),
                Pages.Settings => await SettingsPageAsync(ct),
                _ => PluginViews.Declarative(Ui.EmptyState("unknown-route", "Nothing here")),
            };

            // The notice is the plugin's - it knows what the last button press did, and this
            // knows what the page is. Applied by the caller, over whatever came back.
            return view;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            context.Logger.LogError(exception, "Torrent Downloader could not build the view for {Route}.", request.Route);
            return PageErrorView();
        }
    }

    private async Task<PluginView> SettingsPageAsync(CancellationToken ct)
    {
        LoadedSettings loaded = await settings.LoadAsync(ct);
        IReadOnlyList<string> storedSecretKeys = await context.Secrets.KeysAsync(ct);

        return SettingsView.Build(loaded.Settings, new HashSet<string>(storedSecretKeys, StringComparer.Ordinal));
    }

    // The grant check lives here rather than on the settings page now: a host is asked for
    // on behalf of a source, and the sources page is where an owner can do something about
    // one that has not been granted.
    private async Task<PluginView> SourcesPageAsync(IPluginContext context, CancellationToken ct)
    {
        LoadedSettings loaded = await settings.LoadAsync(ct);
        HostGrants hostGrants = new(context.Grants);
        IReadOnlyList<string> ungrantedHosts = await hostGrants.EnsureAsync(loaded.Settings, ct);
        IReadOnlyList<string> storedSecretKeys = await context.Secrets.KeysAsync(ct);

        return SourcesView.Build(
            loaded.Settings,
            ungrantedHosts,
            new HashSet<string>(storedSecretKeys, StringComparer.Ordinal),
            await HistoryAsync(context, SourcesView.HistoryDepth, ct));
    }

    /// <summary>
    /// One source's own page, reached from the list rather than from the tab bar.
    ///
    /// <para>
    /// A source that is not there any more is not an error worth a stack trace - the list it
    /// was clicked from may simply be a render old, because removing one shifts every index
    /// after it. Saying so and offering the list back is the whole handling.
    /// </para>
    /// </summary>
    private async Task<PluginView> SourcePageAsync(IPluginContext context, string? index, CancellationToken ct)
    {
        LoadedSettings loaded = await settings.LoadAsync(ct);

        if (!int.TryParse(index, out int position) || position < 0 || position >= loaded.Settings.Indexers.Count)
        {
            return Pages.Page(
                Pages.Source,
                "Source",
                0,
                Ui.EmptyState("source-missing", "That source is gone", "It may have been removed since this list was drawn."),
                Ui.Row("source-back", Ui.Button("source-back-button", "Back to sources", Pages.Routes.GoTo(Pages.Sources))));
        }

        IReadOnlyList<string> storedSecretKeys = await context.Secrets.KeysAsync(ct);

        return SourcesView.Detail(
            position,
            loaded.Settings.Indexers[position],
            new HashSet<string>(storedSecretKeys, StringComparer.Ordinal),
            await HistoryAsync(context, SourcesView.HistoryDepth, ct));
    }

    /// <summary>
    /// The shows the plugin is working on, with their counts already worked out.
    ///
    /// <para>
    /// The list is exactly what the last refresh recorded, and nothing is added to it from
    /// anywhere else. An earlier version also took titles from the wanted list, from
    /// history and from a stored list of shows with nothing on the server - which meant a
    /// show the refresh had deliberately passed over reappeared on the page through one of
    /// the other three doors. One question, one answer: whoever decides, records.
    /// </para>
    ///
    /// <para>
    /// Assembled here rather than in the view, for the same reason every other page's data
    /// is: a view that had to join four lists would be a view nothing could assert without
    /// building all four. The joins are by show id throughout - a title is what a reader
    /// matches on, not what a plugin should.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<ShowSummary>> ShowsAsync(IPluginContext context, CancellationToken ct)
    {
        IDownloadStore store = await openStore(ct);

        IReadOnlyList<WantedEpisode> wanted = await store.WantedAsync(int.MaxValue, ct);
        IReadOnlyList<Grab> grabs = await store.ActiveGrabsAsync(ct);
        IReadOnlyList<HistoryEntry> history = await store.HistoryAsync(int.MaxValue, ct);
        HashSet<int> followed = [.. (await settings.LoadAsync(ct)).Settings.FollowedShowIds];

        List<ShowSummary> summaries = [];

        foreach (TrackedShow show in await store.ShowsAsync(ct))
        {
            summaries.Add(new ShowSummary(
                show.ShowId,
                show.Title,
                wanted.Count(episode => episode.Key.ShowId == show.ShowId),
                grabs.Count(grab => grab.Key.ShowId == show.ShowId),
                history
                    .Where(entry => entry.Key.ShowId == show.ShowId && entry.Event == HistoryEvent.Imported)
                    .Select(entry => (DateTimeOffset?)entry.At)
                    .Max(),
                show.Started,
                followed.Contains(show.ShowId))
            {
                Status = show.Status,
                NextAirDate = show.NextAirDate,
            });
        }

        return summaries;
    }

    private async Task<PluginView> ShowPageAsync(IPluginContext context, string? showId, CancellationToken ct)
    {
        IReadOnlyList<ShowSummary> shows = await ShowsAsync(context, ct);

        if (!int.TryParse(showId, out int id) || shows.FirstOrDefault(show => show.ShowId == id) is not { } show)
        {
            return Pages.Page(
                Pages.Show,
                "Show",
                0,
                Ui.EmptyState("show-missing-show", "That show is gone", "The library no longer has it."),
                Ui.Row("show-back", Ui.Button("show-back-button", "Back to shows", Pages.Routes.GoTo(Pages.Shows))));
        }

        IDownloadStore store = await openStore(ct);

        return ShowsView.Detail(
            show,
            [.. (await store.WantedAsync(int.MaxValue, ct)).Where(episode => episode.Key.ShowId == id)],
            [.. (await store.HistoryAsync(int.MaxValue, ct)).Where(entry => entry.Key.ShowId == id)]);
    }

    private async Task<PluginView> OverviewPageAsync(IPluginContext context, CancellationToken ct)
    {
        IDownloadStore store = await openStore(ct);
        HostGrants hostGrants = new(context.Grants);
        LoadedSettings loaded = await settings.LoadAsync(ct);

        return OverviewView.Build(
            await store.TransfersAsync(ct),
            await store.ActiveGrabsAsync(ct),
            await store.WantedAsync(int.MaxValue, ct),
            await store.HistoryAsync(OverviewView.DigestLength, ct),
            await hostGrants.EnsureAsync(loaded.Settings, ct),

            // The count, not the list: the summary line names how many shows those wanted
            // episodes are spread across, and nothing else on this page needs them.
            (await store.ShowsAsync(ct)).Count);
    }

    // Reads the store and nothing else. Deliberately not through PipelineAsync: that builds
    // the engine, and someone opening a page in the dashboard should not be what starts
    // dialling peers. The store is shared with the cadences, so what this shows is what the
    // last transfers tick actually recorded rather than a second, staler copy of it.
    private async Task<PluginView> DownloadsPageAsync(IPluginContext context, CancellationToken ct)
    {
        IDownloadStore store = await openStore(ct);

        return DownloadsView.Build(await store.TransfersAsync(ct), await store.ActiveGrabsAsync(ct));
    }

    /// <summary>
    /// One download, reached by clicking its row.
    ///
    /// <para>
    /// A download that is no longer there is not an error worth a stack trace: it finished
    /// and left the list, or the owner cancelled it in another tab. Saying so and offering
    /// the list back is the whole handling, the same as one source's page.
    /// </para>
    /// </summary>
    private async Task<PluginView> DownloadPageAsync(IPluginContext context, string? infoHash, CancellationToken ct)
    {
        IDownloadStore store = await openStore(ct);

        Transfer? transfer = (await store.TransfersAsync(ct))
            .FirstOrDefault(entry => entry.InfoHash == infoHash);

        if (infoHash is null || transfer is null)
        {
            return Pages.Page(
                Pages.Download,
                "Download",
                0,
                Ui.EmptyState("download-missing", "That download is gone", "It may have finished since this list was drawn."),
                Ui.Row("download-back", Ui.Button("download-back-button", "Back to downloads", Pages.Routes.GoTo(Pages.Downloads))));
        }

        return DownloadsView.Detail(transfer, await store.FindGrabAsync(infoHash, ct));
    }

    private async Task<PluginView> SkippedPageAsync(IPluginContext context, CancellationToken ct)
    {
        IDownloadStore store = await openStore(ct);

        return SkippedView.Build(await store.BlacklistedAsync(clock.UtcNow, ct));
    }

    // Everything still wanted, not a page of it: the view truncates the list itself and says
    // how many it left out, which it cannot do from a list already cut short. The store
    // answers from memory, so the cost of the full list is the allocation.
    private async Task<IReadOnlyList<WantedEpisode>> WantedAsync(IPluginContext context, CancellationToken ct) =>
        await (await openStore(ct)).WantedAsync(int.MaxValue, ct);

    private async Task<IReadOnlyList<HistoryEntry>> HistoryAsync(IPluginContext context, int limit, CancellationToken ct) =>
        await (await openStore(ct)).HistoryAsync(limit, ct);

    // Null-safe before Initialize (the host may dispose a plugin whose load failed) and
    // idempotent (a double dispose is not a bug worth throwing over). Cancelling before
    // flipping _disposed matters no more than the reverse here - both fields are set on the
    // same thread with nothing else observing the gap - but the order documents intent:
}

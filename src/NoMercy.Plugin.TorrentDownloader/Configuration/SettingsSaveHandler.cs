// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Indexers;

namespace NoMercy.Plugin.TorrentDownloader.Configuration;

// SettingsView renders three kinds of form - general, one per indexer, one per private tracker -
// and each posts to its own entry point (see TorrentDownloaderSettingsController): the
// general form to HandleGeneralAsync, an indexer form at render index i to
// HandleIndexerAsync(i, ...), a private tracker's to HandlePrivateTrackerAsync(i, ...).
// A form only ever carries part of the settings world, so each method's job is to load the
// current one, apply just the part this submission addresses, and hand SaveAsync the
// merged result - never the submitted fields alone. Reconstructing settings from the
// submitted fields and saving that directly would wipe every indexer, every tracker and
// every stored credential the moment the owner saves the general form.
//
// The index, not the submitted name, is what identifies the row: the name is the value
// the owner is editing, so a rename means the submitted name and the entry's current name
// legitimately differ. The index is stable across that edit, which is also what makes the
// old name recoverable below for the secret carry-forward, rather than by accident.
public sealed class SettingsSaveHandler(SettingsGateway gateway, IClock clock)
{
    public async Task<SaveSettingsOutcome> HandleGeneralAsync(SaveSettingsRequest request, CancellationToken ct = default) =>
        await PersistIfSucceededAsync(ApplyGeneral(await gateway.LoadAsync(ct), request), ct);

    public async Task<SaveSettingsOutcome> HandleIndexerAsync(int index, SaveSettingsRequest request, CancellationToken ct = default)
    {
        LoadedSettings current = await gateway.LoadAsync(ct);
        return await PersistIfSucceededAsync(ApplyIndexer(current, index, request), ct);
    }

    // No request body: SettingsView's "Add indexer" button carries only the method, so
    // there is nothing here for a submitted field to collide with. See ApplyAddIndexer for
    // why the new entry's name still has to be chosen carefully despite that.
    /// <summary>
    /// Adds a complete source in one go, from wherever the owner happens to be.
    ///
    /// <para>
    /// The settings page adds a blank row and expects it to be filled in afterwards, which
    /// is fine when you are already on the settings page and wrong everywhere else. The
    /// moment somebody realises they need another source is the moment an episode found
    /// nothing - and sending them to a different page to add one is how it does not get
    /// added.
    /// </para>
    /// </summary>
    public async Task<SaveSettingsOutcome> HandleAddSourceAsync(SaveSettingsRequest request, CancellationToken ct = default)
    {
        LoadedSettings current = await gateway.LoadAsync(ct);

        string name = (request.Name ?? string.Empty).Trim();
        string kind = (request.Kind ?? "rss").Trim().ToLowerInvariant();
        string url = (request.Url ?? string.Empty).Trim();

        if (name.Length == 0)
            return SaveSettingsOutcome.Failure("Give the source a name.");

        if (current.Settings.Indexers.Any(indexer => string.Equals(indexer.Name, name, StringComparison.OrdinalIgnoreCase)))
            return SaveSettingsOutcome.Failure($"There is already a source called {name}.");

        if (!IsAbsoluteUrl(url))
            return SaveSettingsOutcome.Failure("The address must be a full URL, starting with https://.");

        if (IsSiteKind(kind) && !SiteIndexer.IsUsableTemplate(url))
        {
            return SaveSettingsOutcome.Failure(
                $"A site's search address needs {SiteIndexer.QueryPlaceholder} where the search terms go.");
        }

        TorrentDownloaderSettings merged = Clone(current.Settings);
        merged.Indexers = [.. current.Settings.Indexers, new IndexerSettings { Name = name, Kind = kind, Url = url }];

        return await PersistIfSucceededAsync(
            SaveSettingsOutcome.Success(new LoadedSettings(merged, [])),
            ct);
    }

    public async Task<SaveSettingsOutcome> HandleAddIndexerAsync(CancellationToken ct = default)
    {
        LoadedSettings current = await gateway.LoadAsync(ct);
        return await PersistIfSucceededAsync(ApplyAddIndexer(current), ct);
    }

    public async Task<SaveSettingsOutcome> HandlePrivateTrackerAsync(int index, SaveSettingsRequest request, CancellationToken ct = default)
    {
        LoadedSettings current = await gateway.LoadAsync(ct);
        return await PersistIfSucceededAsync(ApplyPrivateTracker(current, index, request), ct);
    }

    public async Task<SaveSettingsOutcome> HandleAddPrivateTrackerAsync(CancellationToken ct = default)
    {
        LoadedSettings current = await gateway.LoadAsync(ct);
        return await PersistIfSucceededAsync(ApplyAddPrivateTracker(current), ct);
    }

    public async Task<SaveSettingsOutcome> HandleRemovePrivateTrackerAsync(int index, CancellationToken ct = default)
    {
        LoadedSettings current = await gateway.LoadAsync(ct);
        return await PersistIfSucceededAsync(ApplyRemovePrivateTracker(current, index), ct);
    }

    // Keyed by the library's show id rather than by a render index, unlike every other
    // entry point here: this one is not editing a row the plugin owns, it is naming a
    // show the library owns. An index would be an index into a list that changes shape
    // the moment the show is followed and leaves the "not started" list.
    public async Task<SaveSettingsOutcome> HandleFollowShowAsync(int showId, CancellationToken ct = default)
    {
        LoadedSettings current = await gateway.LoadAsync(ct);
        return await PersistIfSucceededAsync(ApplyFollowShow(current, showId, follow: true), ct);
    }

    public async Task<SaveSettingsOutcome> HandleUnfollowShowAsync(int showId, CancellationToken ct = default)
    {
        LoadedSettings current = await gateway.LoadAsync(ct);
        return await PersistIfSucceededAsync(ApplyFollowShow(current, showId, follow: false), ct);
    }

    public async Task<SaveSettingsOutcome> HandleRemoveIndexerAsync(int index, CancellationToken ct = default)
    {
        LoadedSettings current = await gateway.LoadAsync(ct);
        return await PersistIfSucceededAsync(ApplyRemoveIndexer(current, index), ct);
    }

    // The one place every successful save/add/remove passes through, which is what makes
    // it the one place worth stamping the timestamp - every Apply* method above only needs
    // to get the entry data right, not remember to also touch the clock. Stamped on the
    // merged settings, not the loaded ones, so a failed validation (which never reaches
    // here) leaves the previously saved timestamp exactly as it was.
    private async Task<SaveSettingsOutcome> PersistIfSucceededAsync(SaveSettingsOutcome outcome, CancellationToken ct)
    {
        if (!outcome.Succeeded)
        {
            return outcome;
        }

        outcome.Merged!.Settings.LastSavedAtUtc = clock.UtcNow;
        await gateway.SaveAsync(outcome.Merged!, ct);
        return outcome;
    }

    // No secret changes hands here: none of the four cron fields or the two folders is a
    // secret, and every existing indexer and tracker - and, critically, every secret already
    // stored for them - carries forward untouched because their names are unchanged in the
    // merged settings. SaveAsync's orphan sweep only deletes a secret whose entry's name is
    // absent from what it is handed, so leaving the lists as loaded is what keeps them safe,
    // not an empty-secrets list here (that would only matter if a name had actually changed).
    private static SaveSettingsOutcome ApplyGeneral(LoadedSettings current, SaveSettingsRequest request)
    {
        (string Field, string? Value)[] crons =
        [
            ("Transfers schedule", request.TransfersCron),
            ("Feed schedule", request.FeedCron),
            ("Search schedule", request.SearchCron),
            ("Maintenance schedule", request.MaintenanceCron),
        ];

        foreach ((string field, string? value) in crons)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return SaveSettingsOutcome.Failure($"{field} cannot be blank.");
            }
        }

        // Cloned and then overwritten, like every other Apply, rather than built field by
        // field: built from scratch it forgets whatever was added to the settings last and
        // nothing says so. It had already forgotten the private trackers.
        TorrentDownloaderSettings merged = Clone(current.Settings);

        merged.TransfersCron = request.TransfersCron!;
        merged.FeedCron = request.FeedCron!;
        merged.SearchCron = request.SearchCron!;
        merged.MaintenanceCron = request.MaintenanceCron!;
        merged.IncompleteFolder = request.IncompleteFolder ?? string.Empty;
        merged.IntakeFolder = request.IntakeFolder ?? string.Empty;

        // Falls back to what is stored rather than to false, so a client that omits an
        // off toggle does not silently reset a setting the owner turned on. A submitted
        // false is still honoured - see the save handler test that turns it off again.
        merged.IncludeSpecials = request.IncludeSpecials ?? current.Settings.IncludeSpecials;
        merged.AllowSeasonPacks = request.AllowSeasonPacks ?? current.Settings.AllowSeasonPacks;

        if (!string.IsNullOrWhiteSpace(request.MaximumResolution))
        {
            merged.MaximumResolution = request.MaximumResolution!;
        }

        // Clamped rather than refused. Zero seeders means every dead release is a
        // candidate and the queue fills with downloads that never start; a negative is
        // somebody testing the box. Neither is worth failing a save the owner made for
        // some other reason on the same form.
        if (request.MinimumSeeders is int seeders)
        {
            merged.MinimumSeeders = Math.Clamp(seeders, 1, 1000);
        }

        return SaveSettingsOutcome.Success(new LoadedSettings(merged, []));
    }

    // A rename (name != the entry's current name) is honoured rather than refused: the
    // entry moves to a new key under SaveAsync's orphan sweep because the key derives from
    // the name, so the one deliberate step here is carrying the secret forward to the NEW
    // name whenever the owner did not also submit a new one - otherwise the old key falls
    // out of the live set and SaveAsync deletes it, silently losing the credential on what
    // looked like an ordinary edit.
    private static SaveSettingsOutcome ApplyIndexer(LoadedSettings current, int index, SaveSettingsRequest request)
    {
        if (index < 0 || index >= current.Settings.Indexers.Count)
        {
            return SaveSettingsOutcome.Failure($"No indexer at index {index}.");
        }

        string oldName = current.Settings.Indexers[index].Name;

        if (!IsAbsoluteUrl(request.Url))
        {
            return SaveSettingsOutcome.Failure("Indexer URL must be an absolute URL.");
        }

        // Refused here rather than at search time. A site address without the placeholder
        // searches the same page for every query, which returns the same rows for every
        // episode - a failure that looks like a working site with bad results, and one
        // nobody would think to blame on a missing word in a settings box.
        if (IsSiteKind(request.Kind) && !SiteIndexer.IsUsableTemplate(request.Url))
        {
            return SaveSettingsOutcome.Failure(
                $"A site's search address needs {SiteIndexer.QueryPlaceholder} where the search terms go. "
                + "Search the site once by hand and copy the address, then replace what you searched for.");
        }

        IndexerSettings existing = current.Settings.Indexers[index];
        string newName = string.IsNullOrWhiteSpace(request.Name) ? oldName : request.Name!;

        IndexerSettings updated = new()
        {
            Name = newName,
            Kind = request.Kind ?? existing.Kind,
            Url = request.Url!,
            Priority = request.Priority ?? existing.Priority,
            Enabled = request.Enabled ?? existing.Enabled,
            MinimumIntervalSeconds = request.MinimumIntervalSeconds ?? existing.MinimumIntervalSeconds,
            Categories = ParseCategories(request.Categories),
        };

        List<IndexerSettings> indexers = [.. current.Settings.Indexers];
        indexers[index] = updated;

        TorrentDownloaderSettings merged = Clone(current.Settings);
        merged.Indexers = indexers;

        string? apiKey = string.IsNullOrEmpty(request.ApiKey)
            ? current.IndexerSecrets.FirstOrDefault(secret => secret.Name == oldName)?.ApiKey
            : request.ApiKey;

        List<IndexerSecret> indexerSecrets = string.IsNullOrEmpty(apiKey) ? [] : [new IndexerSecret(newName, apiKey)];

        return SaveSettingsOutcome.Success(new LoadedSettings(merged, indexerSecrets));
    }

    // A new entry's Name starts blank on IndexerSettings and PrivateTrackerSettings, and two
    // blank names would derive the exact same secret key (SettingsGateway.IndexerSecretKey
    // hashes only the name) - the second Add would silently share, then clobber, the
    // first entry's stored credential the moment either got an API key. Naming the entry
    // here instead removes the collision at its source rather than validating for it
    // later. Never persisted unedited forever - it is what the owner sees in the form's
    // Name field and is expected to replace, same as any other default.
    private static string NextDefaultName(string prefix, IReadOnlyCollection<string> existingNames)
    {
        HashSet<string> taken = new(existingNames, StringComparer.OrdinalIgnoreCase);

        int suffix = existingNames.Count + 1;
        string candidate = $"{prefix} {suffix}";
        while (taken.Contains(candidate))
        {
            suffix++;
            candidate = $"{prefix} {suffix}";
        }

        return candidate;
    }

    // No validation to fail here, unlike ApplyIndexer: an added entry's Url is
    // blank, which is expected - the owner fills it in and saves through the per-entry form
    // afterward, the same form that does enforce an absolute URL. Secrets are passed through
    // untouched ([], []) rather than carried forward explicitly, for the same reason
    // ApplyGeneral does: every existing entry's name is unchanged in the merged settings, so
    // SaveAsync's orphan sweep leaves their stored secrets alone on its own.
    private static SaveSettingsOutcome ApplyAddIndexer(LoadedSettings current)
    {
        IndexerSettings added = new()
        {
            Name = NextDefaultName("New Indexer", [.. current.Settings.Indexers.Select(indexer => indexer.Name)]),
        };

        TorrentDownloaderSettings merged = Clone(current.Settings);
        merged.Indexers = [.. current.Settings.Indexers, added];

        return SaveSettingsOutcome.Success(new LoadedSettings(merged, []));
    }

    // Deleting the entry from the merged settings is the whole fix: SaveAsync's orphan
    // sweep deletes exactly the secrets whose entry's name is no longer live, and after
    // this the removed entry's name is not live. No explicit secret deletion belongs here
    // - that would be a second, competing way to reach the same result, and the two could
    // drift (e.g. a rename in flight) in a way a single sweep cannot.
    private static SaveSettingsOutcome ApplyRemoveIndexer(LoadedSettings current, int index)
    {
        if (index < 0 || index >= current.Settings.Indexers.Count)
        {
            return SaveSettingsOutcome.Failure($"No indexer at index {index}.");
        }

        List<IndexerSettings> indexers = [.. current.Settings.Indexers];
        indexers.RemoveAt(index);

        TorrentDownloaderSettings merged = Clone(current.Settings);
        merged.Indexers = indexers;

        return SaveSettingsOutcome.Success(new LoadedSettings(merged, []));
    }

    // Unlike an indexer's, the URL being edited here is one this form can never
    // show back, because the passkey in it is the account. So blank means "keep what is
    // stored", and the entry is only refused when neither the submission nor the store
    // has one - an entry with no announce URL matches no torrent and therefore quietly
    // makes nothing private, which is the failure worth being loud about.
    private static SaveSettingsOutcome ApplyPrivateTracker(LoadedSettings current, int index, SaveSettingsRequest request)
    {
        if (index < 0 || index >= current.Settings.PrivateTrackers.Count)
        {
            return SaveSettingsOutcome.Failure($"No private tracker at index {index}.");
        }

        PrivateTrackerSettings existing = current.Settings.PrivateTrackers[index];
        string oldName = existing.Name;
        string newName = string.IsNullOrWhiteSpace(request.Name) ? oldName : request.Name!;

        PrivateTrackerSecret? stored = current.PrivateTrackerSecrets.FirstOrDefault(secret => secret.Name == oldName);

        string? announceUrl = string.IsNullOrWhiteSpace(request.AnnounceUrl) ? stored?.AnnounceUrl : request.AnnounceUrl;

        if (string.IsNullOrWhiteSpace(announceUrl))
        {
            return SaveSettingsOutcome.Failure("A private tracker needs its announce URL.");
        }

        if (!IsAbsoluteUrl(announceUrl))
        {
            return SaveSettingsOutcome.Failure("Private tracker announce URL must be an absolute URL.");
        }

        PrivateTrackerSettings updated = new()
        {
            Name = newName,
            Enabled = request.Enabled ?? existing.Enabled,
            Seed = request.Seed ?? existing.Seed,
            SeedRatioTarget = request.SeedRatioTarget ?? existing.SeedRatioTarget,
            SeedTimeTargetHours = request.SeedTimeTargetHours ?? existing.SeedTimeTargetHours,
        };

        List<PrivateTrackerSettings> trackers = [.. current.Settings.PrivateTrackers];
        trackers[index] = updated;

        TorrentDownloaderSettings merged = Clone(current.Settings);
        merged.PrivateTrackers = trackers;

        string? apiKey = string.IsNullOrEmpty(request.ApiKey) ? stored?.ApiKey : request.ApiKey;

        return SaveSettingsOutcome.Success(
            new LoadedSettings(merged, [], [new PrivateTrackerSecret(newName, announceUrl, apiKey)]));
    }

    private static SaveSettingsOutcome ApplyAddPrivateTracker(LoadedSettings current)
    {
        // Seed stays off, which PrivateTrackerSettings' own default already says. Adding
        // a tracker is not consenting to upload from it; that is a second decision, made
        // on the entry's own form.
        PrivateTrackerSettings added = new()
        {
            Name = NextDefaultName("New Private Tracker", [.. current.Settings.PrivateTrackers.Select(tracker => tracker.Name)]),
        };

        TorrentDownloaderSettings merged = Clone(current.Settings);
        merged.PrivateTrackers = [.. current.Settings.PrivateTrackers, added];

        return SaveSettingsOutcome.Success(new LoadedSettings(merged, []));
    }

    private static SaveSettingsOutcome ApplyRemovePrivateTracker(LoadedSettings current, int index)
    {
        if (index < 0 || index >= current.Settings.PrivateTrackers.Count)
        {
            return SaveSettingsOutcome.Failure($"No private tracker at index {index}.");
        }

        List<PrivateTrackerSettings> trackers = [.. current.Settings.PrivateTrackers];
        trackers.RemoveAt(index);

        TorrentDownloaderSettings merged = Clone(current.Settings);
        merged.PrivateTrackers = trackers;

        return SaveSettingsOutcome.Success(new LoadedSettings(merged, []));
    }

    // Idempotent in both directions on purpose. These are driven by a button on a page
    // that may be a refresh interval behind the truth, so following a show twice, or
    // unfollowing one that is not followed, is an ordinary race and not an error worth
    // showing anybody.
    private static SaveSettingsOutcome ApplyFollowShow(LoadedSettings current, int showId, bool follow)
    {
        List<int> followed = [.. current.Settings.FollowedShowIds];

        if (follow && !followed.Contains(showId))
        {
            followed.Add(showId);
        }
        else if (!follow)
        {
            followed.Remove(showId);
        }

        TorrentDownloaderSettings merged = Clone(current.Settings);
        merged.FollowedShowIds = followed;

        return SaveSettingsOutcome.Success(new LoadedSettings(merged, []));
    }

    private static bool IsSiteKind(string? kind) =>
        kind?.Trim().Equals("site", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsAbsoluteUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out _);

    private static List<string> ParseCategories(string? categories) =>
        string.IsNullOrWhiteSpace(categories)
            ? []
            : [.. categories.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    // Copies everything, lists included, and each caller then replaces the one list its
    // form is about.
    //
    // It used to copy everything EXCEPT the lists, leaving every caller to carry the ones
    // it was not editing. That shape has a standing invitation to a bug in it: add a
    // setting and eight methods have to remember it, and the one that forgets resets the
    // owner's choice during an edit of something unrelated. It was not hypothetical -
    // IncludeSpecials was added and immediately turned itself off whenever an indexer was
    // saved. Copying everything means a new setting survives by default and only a
    // deliberate line can drop it.
    private static TorrentDownloaderSettings Clone(TorrentDownloaderSettings source) =>
        new()
        {
            TransfersCron = source.TransfersCron,
            FeedCron = source.FeedCron,
            SearchCron = source.SearchCron,
            MaintenanceCron = source.MaintenanceCron,
            IncompleteFolder = source.IncompleteFolder,
            IntakeFolder = source.IntakeFolder,
            IncludeSpecials = source.IncludeSpecials,
            MaximumResolution = source.MaximumResolution,
            MinimumSeeders = source.MinimumSeeders,
            AllowSeasonPacks = source.AllowSeasonPacks,
            Indexers = source.Indexers,
            PrivateTrackers = source.PrivateTrackers,
            FollowedShowIds = source.FollowedShowIds,
        };
}

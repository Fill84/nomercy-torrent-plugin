using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Configuration;

public class SettingsStoreTests : IDisposable
{
    /// <remarks>
    /// <para>
    /// A folder the server cannot write to says where it can. The check itself
    /// is stronger than any list — it creates the folder and writes a real file
    /// into it — but "it cannot be written" is something the owner can only
    /// read, and the names of the places that would work are something they can
    /// act on.
    /// </para>
    /// <para>
    /// media-server #32, opened by this plugin and naming this exact case: the
    /// intake folder is a string typed on whatever machine the server happens
    /// to be. Writing <em>through</em> the facade is a different thing and not
    /// this: the encode is asked for with an absolute path, so a staged file on
    /// a remote location could not be named to the encoder at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFolderTheServerCannotWriteToSaysWhereItCan()
    {
        FakePluginContext context = new();

        SettingsStore store = new(
            context.Config,
            context.Secrets,
            volumeOf: _ => @"C:\",
            storage: () => new TwoPlaces());

        Settings settings = new()
        {
            // A path no machine has, so the write probe refuses it and nothing
            // in this test depends on which drives the runner happens to carry.
            IncompleteFolder = Path.Combine(Path.GetTempPath(), "nomercy-nowhere", "\u0000"),
            IntakeFolder = Path.Combine(Path.GetTempPath(), "nomercy-intake-" + Guid.NewGuid().ToString("n")[..8]),
        };

        SaveResult result = await store.SaveAsync(settings, CancellationToken.None);

        Assert.False(result.Saved);
        Assert.Contains(result.Errors, one => one.Contains("Media (local)", StringComparison.Ordinal));

        // And not the one it cannot write to, because a place that is no use is
        // not a suggestion.
        Assert.DoesNotContain(result.Errors, one => one.Contains("Archive", StringComparison.Ordinal));
    }

    /// <summary>A server with one place it can write and one it cannot.</summary>
    private sealed class TwoPlaces : IPluginStorage
    {
        public Task<IReadOnlyList<PluginStorageLocation>> LocationsAsync(CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<PluginStorageLocation>>(
            [
                new("01", "Media", "local", Writable: true),
                new("02", "Archive", "s3", Writable: false),
            ]);
        }

        public Task<IPluginStorageScope?> OpenAsync(string locationId, CancellationToken ct = default)
        {
            return Task.FromResult<IPluginStorageScope?>(null);
        }
    }

    private readonly List<string> _folders = [];

    /// <remarks>
    /// Every default in docs/04-domain.md § Settings, through a real save and a
    /// real load. A default that survives in memory but not through the host's
    /// serialiser is a default the owner never gets — and a plugin behaving
    /// unlike its own documentation is how an owner comes to trust neither.
    /// </remarks>
    [Fact]
    public async Task EverySettingRoundTripsWithItsDocumentedDefault()
    {
        FakePluginContext context = new();
        SettingsStore store = new(context.Config, context.Secrets);

        SaveResult saved = await store.SaveAsync(Writable(new Settings()), CancellationToken.None);
        Assert.True(saved.Saved, string.Join("; ", saved.Errors));

        Settings settings = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("* * * * *", settings.Cadences.Transfers);
        Assert.Equal("*/15 * * * *", settings.Cadences.Feed);
        Assert.Equal("0 */6 * * *", settings.Cadences.Search);
        Assert.Equal("0 4 * * *", settings.Cadences.Maintenance);

        Assert.False(settings.Profile.IncludeSpecials);
        Assert.Equal("1080p", settings.Profile.MaximumResolution);
        Assert.Equal("any", settings.Profile.Codec);
        Assert.True(settings.Profile.RequireCodecTag);
        Assert.True(settings.Profile.EnglishOnly);
        Assert.Empty(settings.Profile.ExcludeTerms);
        Assert.Equal(2, settings.Profile.MinimumSeeders);
        Assert.True(settings.Profile.AllowSeasonPacks);
        Assert.Equal(3, settings.Profile.SeasonPackThreshold);
        Assert.Equal(3, settings.Profile.MaxSearchAttempts);

        Assert.Equal(5, settings.Client.MaxConcurrentDownloads);
        Assert.Empty(settings.Client.DefaultTrackers);
        Assert.Equal(51413, settings.Client.ListenPort);
        Assert.True(settings.Client.PortMapping);
        Assert.Equal(0, settings.Client.MaxDownloadRate);
        Assert.Equal(0, settings.Client.MaxUploadRate);
        Assert.Equal(1.0, settings.Client.SeedRatio);
        Assert.Equal(48, settings.Client.SeedHours);
        Assert.Equal(30, settings.Client.StallMinutes);
        Assert.Equal(5, settings.Client.MetadataTimeoutMinutes);
        Assert.Equal(EncryptionPolicy.Allowed, settings.Client.Encryption);

        Assert.Empty(settings.Indexers);
        Assert.Empty(settings.PrivateTrackers);
        Assert.Empty(settings.DisabledDefaultSources);
        Assert.False(settings.DryRun);
    }

    /// <remarks>
    /// A plugin that has never been configured has to answer with the
    /// documented defaults rather than with a folder of empty strings, or every
    /// caller has to know what each default was.
    /// </remarks>
    [Fact]
    public async Task LoadingBeforeAnythingWasEverSavedGivesTheDefaults()
    {
        FakePluginContext context = new();
        SettingsStore store = new(context.Config, context.Secrets);

        Settings settings = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("0 4 * * *", settings.Cadences.Maintenance);
        Assert.Equal(51413, settings.Client.ListenPort);
    }

    /// <remarks>
    /// The stored value is left alone. A refused save that had already
    /// half-written would leave the plugin running settings the owner never
    /// agreed to and the page showing the ones they typed.
    /// </remarks>
    [Fact]
    public async Task AnInvalidCronIsRefusedWithTheReasonAndChangesNothing()
    {
        FakePluginContext context = new();
        SettingsStore store = new(context.Config, context.Secrets);
        await store.SaveAsync(Writable(new Settings()), CancellationToken.None);

        Settings broken = Writable(new Settings());
        broken.Cadences.Search = "0 */6 * *";
        broken.Client.ListenPort = 6881;

        SaveResult result = await store.SaveAsync(broken, CancellationToken.None);

        Assert.False(result.Saved);
        Assert.Contains(result.Errors, error => error.Contains("search", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("five fields", StringComparison.OrdinalIgnoreCase));

        Settings stored = await store.LoadAsync(CancellationToken.None);
        Assert.Equal("0 */6 * * *", stored.Cadences.Search);
        Assert.Equal(51413, stored.Client.ListenPort);
    }

    /// <remarks>
    /// A folder that cannot be written is found now, on the page, rather than
    /// at three in the morning when a finished transfer has nowhere to go.
    /// </remarks>
    [Fact]
    public async Task AFolderThatCannotBeWrittenIsRefusedWithTheReason()
    {
        FakePluginContext context = new();
        SettingsStore store = new(context.Config, context.Secrets);

        // A file where a folder should be: it exists, and it can never be one.
        string file = Path.Combine(Folder(), "not-a-folder");
        await File.WriteAllTextAsync(file, "x", CancellationToken.None);

        Settings settings = Writable(new Settings());
        settings.IncompleteFolder = file;

        SaveResult result = await store.SaveAsync(settings, CancellationToken.None);

        Assert.False(result.Saved);
        Assert.Contains(result.Errors, error => error.Contains(file, StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("incomplete", StringComparison.OrdinalIgnoreCase));
    }

    /// <remarks>
    /// A warning, not a refusal. Two volumes is a working configuration that
    /// costs a full-file copy on every completion, and the owner may well have
    /// meant it — a fast disk for downloading, a large one for the library.
    /// </remarks>
    [Fact]
    public async Task FoldersOnDifferentVolumesSaveWithAWarning()
    {
        FakePluginContext context = new();
        SettingsStore store = new(
            context.Config,
            context.Secrets,
            // The two temporary folders are on one volume, so the seam says
            // what a two-volume machine would have said. Asserting this against
            // real drive letters would pass or fail on which machine ran it.
            volumeOf: path => path);

        SaveResult result = await store.SaveAsync(Writable(new Settings()), CancellationToken.None);

        Assert.True(result.Saved, string.Join("; ", result.Errors));
        Assert.Contains(result.Warnings, warning => warning.Contains("volume", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Warnings, warning => warning.Contains("copy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FoldersOnOneVolumeSaveWithNoWarning()
    {
        FakePluginContext context = new();
        SettingsStore store = new(context.Config, context.Secrets, volumeOf: _ => "one-volume");

        SaveResult result = await store.SaveAsync(Writable(new Settings()), CancellationToken.None);

        Assert.True(result.Saved, string.Join("; ", result.Errors));
        Assert.Empty(result.Warnings);
    }

    /// <remarks>
    /// Finding out whether a folder can be written means writing to it, and
    /// what is written has to be taken away again. A probe left behind would
    /// drop a file into the download folder on every save — into a folder whose
    /// whole contract is that only video files are written there.
    /// </remarks>
    [Fact]
    public async Task CheckingAFolderLeavesNothingInIt()
    {
        FakePluginContext context = new();
        SettingsStore store = new(context.Config, context.Secrets);
        Settings settings = Writable(new Settings());

        await store.SaveAsync(settings, CancellationToken.None);
        await store.SaveAsync(settings, CancellationToken.None);

        Assert.Empty(Directory.GetFileSystemEntries(settings.IncompleteFolder));
        Assert.Empty(Directory.GetFileSystemEntries(settings.IntakeFolder));
    }

    /// <remarks>
    /// A passkey and an API key never travel in the settings blob: that is
    /// whole-object JSON on disk, so a secret written through it lands in
    /// plaintext beside everything else.
    /// </remarks>
    [Fact]
    public async Task ASecretGoesToTheSecretStoreAndNeverIntoTheSettings()
    {
        FakePluginContext context = new();
        SettingsStore store = new(context.Config, context.Secrets);

        Settings settings = Writable(new Settings());
        settings.Indexers.Add(new() { Id = "own-1", Name = "Mine", Address = "https://x/?q={query}" });

        await store.SaveAsync(settings, CancellationToken.None);
        await store.SetSecretAsync(SettingsStore.IndexerApiKey("own-1"), "hunter2", CancellationToken.None);

        Assert.DoesNotContain("hunter2", context.Config.Written, StringComparison.Ordinal);
        Assert.Equal("hunter2", await context.Secrets.GetAsync(SettingsStore.IndexerApiKey("own-1")));
        Assert.Contains(SettingsStore.IndexerApiKey("own-1"), await store.SecretsSetAsync(CancellationToken.None));
    }

    /// <remarks>
    /// <para>
    /// <strong>The settings are read from the host once, not on every ask.</strong>
    /// The transfers cadence runs every minute and every page draws from them,
    /// so this is a read of data that changes only when an owner presses save,
    /// asked for at least once a minute for as long as the plugin runs.
    /// </para>
    /// <para>
    /// Nothing about the cost of it shows in an outcome, which is why this
    /// counts. What the cache must never do is shown by the test below it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheSettingsAreReadFromTheHostOnce()
    {
        FakePluginContext context = new();
        SettingsStore store = new(context.Config, context.Secrets);

        await store.SaveAsync(Writable(new Settings()), CancellationToken.None);

        int afterSaving = context.Config.Reads;

        await store.LoadAsync(CancellationToken.None);
        await store.LoadAsync(CancellationToken.None);
        await store.LoadAsync(CancellationToken.None);

        Assert.Equal(afterSaving + 1, context.Config.Reads);
    }

    /// <remarks>
    /// <para>
    /// <strong>A save is seen by the next load.</strong> A stale settings cache
    /// is worse than the round trip it saves: an owner who changes the intake
    /// folder, or turns a source off, and watches the plugin carry on with the
    /// old answer has no way to tell that from the setting not working at all.
    /// </para>
    /// <para>
    /// So the cache is dropped by the save, and this is the test that says so.
    /// It is the reason the caching is allowed to exist.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASaveIsSeenByTheNextLoad()
    {
        FakePluginContext context = new();
        SettingsStore store = new(context.Config, context.Secrets);

        await store.SaveAsync(Writable(new Settings()), CancellationToken.None);

        // Read once, so anything remembering an answer has one to give.
        Assert.Equal(5, (await store.LoadAsync(CancellationToken.None)).Client.MaxConcurrentDownloads);

        Settings changed = Writable(new Settings());
        changed.Client.MaxConcurrentDownloads = 9;

        SaveResult saved = await store.SaveAsync(changed, CancellationToken.None);

        Assert.True(saved.Saved, string.Join("; ", saved.Errors));
        Assert.Equal(9, (await store.LoadAsync(CancellationToken.None)).Client.MaxConcurrentDownloads);
    }

    /// <remarks>
    /// A save that was refused changes nothing, so the answer the next load
    /// gives is the one that is really stored. A cache dropped by an attempt
    /// rather than by a write would go and fetch the same thing again; a cache
    /// left holding what the refused save proposed would be worse still.
    /// </remarks>
    [Fact]
    public async Task ASaveThatWasRefusedLeavesTheStoredSettingsAlone()
    {
        FakePluginContext context = new();
        SettingsStore store = new(context.Config, context.Secrets);

        await store.SaveAsync(Writable(new Settings()), CancellationToken.None);

        Settings wrong = Writable(new Settings());
        wrong.Client.MaxConcurrentDownloads = 9;
        wrong.Cadences.Transfers = "not a cron";

        Assert.False((await store.SaveAsync(wrong, CancellationToken.None)).Saved);

        Assert.Equal(5, (await store.LoadAsync(CancellationToken.None)).Client.MaxConcurrentDownloads);
    }

    /// <remarks>
    /// <para>
    /// <strong>What a load hands back is the caller's own to change.</strong>
    /// The settings page loads, applies what the owner typed and saves — and
    /// when any field is refused, nothing is saved at all. That is the whole
    /// point of refusing: the owner is not left looking at a page where some of
    /// what they typed took and some did not.
    /// </para>
    /// <para>
    /// A load that handed back one shared object would break that. The refused
    /// edit would still be sitting in it, so every other part of the plugin
    /// would run on values the owner was told had not been accepted, until
    /// something else saved. Nothing would say so and nothing on disk would
    /// show it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARefusedEditIsNotLeftBehindInWhatTheNextLoadGives()
    {
        FakePluginContext context = new();
        SettingsStore store = new(context.Config, context.Secrets);

        await store.SaveAsync(Writable(new Settings()), CancellationToken.None);

        // The settings page: load, then apply what was typed.
        Settings typed = await store.LoadAsync(CancellationToken.None);

        IReadOnlyList<string> refused = SettingsEdit.Apply(
            typed,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["client.maxConcurrentDownloads"] = "9",
                ["there.is.no.such.setting"] = "9",
            });

        // One field was refused, so the controller saves nothing at all.
        Assert.NotEmpty(refused);

        Assert.Equal(5, (await store.LoadAsync(CancellationToken.None)).Client.MaxConcurrentDownloads);
    }

    private Settings Writable(Settings settings)
    {
        settings.IncompleteFolder = Folder();
        settings.IntakeFolder = Folder();
        return settings;
    }

    private string Folder()
    {
        string path = Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        _folders.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (string folder in _folders.Where(Directory.Exists))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <remarks>
    /// <para>
    /// <strong>A save that succeeds with a warning says the warning.</strong>
    /// The store decides that two folders on different volumes make every
    /// completion a full-file copy rather than a rename — minutes of disk on a
    /// season pack. Then nothing read <c>Warnings</c> at all, so the owner
    /// saved, saw "ok", and was never told.
    /// </para>
    /// <para>
    /// Written down and never read: the same shape as the cadence fields that
    /// changed no schedule and the refusal that never reached the pipeline.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASaveSaysItsWarningsWhenItSucceededAndItsReasonsWhenItDidNot()
    {
        Assert.Equal(
            "Different volumes, so every completion is a copy.",
            new SaveResult(true, [], ["Different volumes, so every completion is a copy."]).Said());

        Assert.Equal(
            "The feed cadence is not a cron.",
            new SaveResult(false, ["The feed cadence is not a cron."], []).Said());

        // And a save with nothing to say says nothing, rather than an empty
        // string the page would draw as a blank line under the form.
        Assert.Null(new SaveResult(true, [], []).Said());
    }
}

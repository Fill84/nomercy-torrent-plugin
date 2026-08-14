using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Configuration;

public class SettingsStoreTests : IDisposable
{
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
}

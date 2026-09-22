using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.PluginSdk.Abstractions;
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
            places: () => TwoPlaces);

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

    /// <summary>A server with one place it can write and one it cannot, as <c>IPluginServerInfo.GrantedPaths</c> lists them.</summary>
    private static IReadOnlyList<PluginStorageLocation> TwoPlaces =>
    [
        new("01", "Media", "local", Writable: true),
        new("02", "Archive", "s3", Writable: false),
    ];

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

        Assert.Equal("0 * * * *", settings.Cadences.Cycle);

        Assert.Equal(5, settings.Client.MaxConcurrentDownloads);
        Assert.Empty(settings.Client.DefaultTrackers);
        Assert.Equal(6881, settings.Client.ListenPort);
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
    }

    /// <remarks>
    /// <para>
    /// <strong>The owner's own <c>config.json</c> survives sprint 12.</strong>
    /// It was written by a version that still had all six of these, and a
    /// settings file is read into <see cref="Settings"/> field by field: a key
    /// the type no longer has is simply not there to bind to, so it is ignored
    /// rather than refusing the whole file. <c>PortMapping</c> is not one of
    /// the six yet — its own slice has not run — so it is asserted as a real
    /// setting here, not as a leftover key.
    /// </para>
    /// <para>
    /// The second half is what makes the first half worth anything: a save is
    /// always a fresh serialise of <see cref="Settings"/>, so a key the type no
    /// longer carries cannot survive one. Proved once here rather than assumed,
    /// because a removed setting that lingered in the file forever would still
    /// pass every other test in this class.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASettingsFileFromAnOlderVersionStillLoads()
    {
        FakePluginContext context = new();
        SettingsStore store = new(context.Config, context.Secrets);

        string incomplete = Folder();
        string intake = Folder();

        // The shape an older version wrote: everything this version still
        // has, plus the six keys an owner's real file may still carry.
        context.Config.SaveConfiguration(new
        {
            IncompleteFolder = incomplete,
            IntakeFolder = intake,
            Profile = new
            {
                MaximumResolution = "1080p",
                MaxSearchAttempts = 3,
                MinimumSeeders = 1,
                SeasonPackThreshold = 2,
                AllowSeasonPacks = true,
            },
            Client = new
            {
                ListenPort = 51413,
                PortMapping = true,
            },
            DryRun = true,
        });

        Settings settings = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(incomplete, settings.IncompleteFolder);
        Assert.Equal(intake, settings.IntakeFolder);
        Assert.Equal(51413, settings.Client.ListenPort);

        SaveResult saved = await store.SaveAsync(settings, CancellationToken.None);
        Assert.True(saved.Saved, string.Join("; ", saved.Errors));

        Assert.DoesNotContain("DryRun", context.Config.Written, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxSearchAttempts", context.Config.Written, StringComparison.Ordinal);
        Assert.DoesNotContain("MinimumSeeders", context.Config.Written, StringComparison.Ordinal);
        Assert.DoesNotContain("SeasonPackThreshold", context.Config.Written, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowSeasonPacks", context.Config.Written, StringComparison.Ordinal);

        // S12-06 took the switch out. The owner's own file still carries the
        // key — beast-unit's says PortMapping: true — so the load has to walk
        // past it rather than refuse the file, and the save must not write it
        // back and make it look like a setting that still does something.
        Assert.DoesNotContain("PortMapping", context.Config.Written, StringComparison.Ordinal);
    }

    /// <remarks>
    /// <c>S13-09</c>: the global profile is gone — quality, codec, tags and language are set per show and per
    /// library (<c>docs/specs/show-list.md</c>). The owner's own file still carries it, written by every earlier
    /// version, so it has to load with the profile walked past, and the next save must not write it back and
    /// make it look like a setting that still does something.
    /// </remarks>
    [Fact]
    public async Task SettingsSavedWithAProfileLoadWithoutIt()
    {
        FakePluginContext context = new();
        SettingsStore store = new(context.Config, context.Secrets);

        string incomplete = Folder();
        string intake = Folder();

        context.Config.SaveConfiguration(new
        {
            IncompleteFolder = incomplete,
            IntakeFolder = intake,
            Profile = new
            {
                MaximumResolution = "2160p",
                Codec = "h265",
                RequireCodecTag = true,
                EnglishOnly = true,
                IncludeSpecials = true,
                ExcludeTerms = new[] { "HDCAM" },
            },
        });

        Settings settings = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(incomplete, settings.IncompleteFolder);

        SaveResult saved = await store.SaveAsync(settings, CancellationToken.None);
        Assert.True(saved.Saved, string.Join("; ", saved.Errors));

        foreach (string gone in (string[])["Profile", "MaximumResolution", "EnglishOnly", "RequireCodecTag", "ExcludeTerms", "IncludeSpecials"])
        {
            Assert.DoesNotContain(gone, context.Config.Written, StringComparison.Ordinal);
        }
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

        Assert.Equal("0 * * * *", settings.Cadences.Cycle);
        Assert.Equal(6881, settings.Client.ListenPort);
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
        broken.Cadences.Cycle = "0 */6 * *";
        broken.Client.ListenPort = 6999;

        SaveResult result = await store.SaveAsync(broken, CancellationToken.None);

        Assert.False(result.Saved);
        Assert.Contains(result.Errors, error => error.Contains("cycle", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("five fields", StringComparison.OrdinalIgnoreCase));

        Settings stored = await store.LoadAsync(CancellationToken.None);
        Assert.Equal("0 * * * *", stored.Cadences.Cycle);
        Assert.Equal(6881, stored.Client.ListenPort);
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
    /// Every transfers pass, every cycle and every page draws from them, so this
    /// is a read of data that changes only when an owner presses save, asked for
    /// far more often than that for as long as the plugin runs.
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
        wrong.Cadences.Cycle = "not a cron";

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

    /// <remarks>
    /// Refused where the owner saves it, with the reason, and the stored cadence
    /// is left as it was — the same as a cron that is not a cron. The raw box
    /// under Show advanced goes through here too, so typing one by hand is no way
    /// round it.
    /// </remarks>
    [Fact]
    public async Task AnIntervalShorterThanFifteenMinutesIsRefusedAndChangesNothing()
    {
        FakePluginContext context = new();
        SettingsStore store = new(context.Config, context.Secrets);
        await store.SaveAsync(Writable(new Settings()), CancellationToken.None);

        Settings eager = Writable(new Settings());
        eager.Cadences.Cycle = "*/5 * * * *";

        SaveResult result = await store.SaveAsync(eager, CancellationToken.None);

        Assert.False(result.Saved);
        Assert.Contains(result.Errors, error => error.Contains("15 minutes", StringComparison.Ordinal));

        Settings stored = await store.LoadAsync(CancellationToken.None);
        Assert.Equal("0 * * * *", stored.Cadences.Cycle);
    }

    /// <remarks>
    /// The shortest interval <c>docs/specs/release-names.md</c> accepts, and it is accepted.
    /// </remarks>
    [Fact]
    public async Task FifteenMinutesIsAccepted()
    {
        FakePluginContext context = new();
        SettingsStore store = new(context.Config, context.Secrets);

        Settings quarterly = Writable(new Settings());
        quarterly.Cadences.Cycle = "*/15 * * * *";

        SaveResult result = await store.SaveAsync(quarterly, CancellationToken.None);

        Assert.True(result.Saved, string.Join("; ", result.Errors));
        Assert.Equal("*/15 * * * *", (await store.LoadAsync(CancellationToken.None)).Cadences.Cycle);
    }

    /// <remarks>
    /// <para>
    /// <strong>The owner's own settings, as they are on beast-unit today.</strong>
    /// They were saved with four cadences — transfers every minute, feed every
    /// fifteen, search every six hours, maintenance at four — and the upgrade
    /// that makes those one must load them rather than fail on them.
    /// </para>
    /// <para>
    /// The retired four are not read into anything, and the cycle comes out at
    /// its default of hourly: none of the four was ever a cadence for starting a
    /// cycle, so carrying one of them over would be guessing which. And saving
    /// again writes none of them back.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SettingsSavedWithFourCadencesLoadAsOneHourlyCycle()
    {
        FakePluginContext context = new();

        context.Config.SaveConfiguration(new
        {
            IncompleteFolder = @"D:\incomplete",
            IntakeFolder = @"D:\intake",
            Cadences = new
            {
                Transfers = "* * * * *",
                Feed = "*/15 * * * *",
                Search = "0 */6 * * *",
                Maintenance = "0 4 * * *",
            },
        });

        SettingsStore store = new(context.Config, context.Secrets);

        Settings loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("0 * * * *", loaded.Cadences.Cycle);
        Assert.Equal(@"D:\incomplete", loaded.IncompleteFolder);

        await store.SaveAsync(Writable(loaded), CancellationToken.None);

        Assert.DoesNotContain("Transfers", context.Config.Written, StringComparison.Ordinal);
        Assert.DoesNotContain("Maintenance", context.Config.Written, StringComparison.Ordinal);
    }
}

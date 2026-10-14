using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

/// <summary>
/// A passkey and an API key never leave protected storage.
/// </summary>
/// <remarks>
/// <para>
/// This is a rule from the working agreement rather than a protocol detail, and
/// it is swept rather than spot-checked: every page the plugin can render,
/// every log line it writes and every journal entry it keeps, searched for the
/// secrets themselves.
/// </para>
/// <para>
/// A test that asserted one page hides one field would pass while a secret went
/// out in an error message from somewhere else entirely — which is exactly how
/// a passkey ends up in a screenshot in a support thread.
/// </para>
/// </remarks>
public class SecretsNeverEscapeTests
{
    /// <remarks>
    /// Every route the plugin serves, rendered with a passkey and an API key in
    /// the store, and read the way a person would read the page.
    /// </remarks>
    [Fact]
    public async Task NoPageEverRendersASecret()
    {
        using TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = Seeded();

        plugin.Initialize(context);

        foreach (string route in Routes)
        {
            PluginView view = await plugin.GetViewAsync(Requests.View(route), CancellationToken.None);

            string page = string.Join(" ", Rendered.Words(view));

            // Not only the words a reader is shown: every value under every
            // prop, however deeply nested, plus every component's id. A secret
            // written into a field's value is on screen in the browser whether
            // or not anything renders it as text.
            string everything = string.Join(
                " ",
                [page, .. Rendered.EveryValue(view), .. Rendered.All(view).Select(one => one.Id)]);

            foreach (string secret in Secrets)
            {
                Assert.DoesNotContain(secret, everything, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <remarks>
    /// <para>
    /// The Settings page has to say whether a passkey is <em>set</em> — an owner
    /// cannot otherwise tell a tracker that is configured from one that is not —
    /// and saying so is not the same as saying what it is. The announce address
    /// is shown as well, with <c>{passkey}</c> standing where the secret goes.
    /// </para>
    /// <para>
    /// This is the assertion that keeps the page useful, so that the sweep above
    /// cannot be passed by a page that renders nothing at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePageSaysAPasskeyIsSetWithoutSayingWhatItIs()
    {
        Settings settings = new()
        {
            PrivateTrackers =
            [
                new()
                {
                    Id = "some-tracker",
                    Host = "tracker.example",
                    AnnounceTemplate = "https://tracker.example/announce/{passkey}",
                },
            ],
        };

        PluginView view = SettingsView.Render(
            settings,
            [SettingsStore.TrackerPasskey("some-tracker")],
            []);

        // The field is there, and it knows the secret exists.
        Assert.Contains(
            Rendered.All(view),
            one => one.Id == $"tracker-some-tracker-passkey");

        string everything = string.Join(" ", [.. Rendered.Words(view), .. Rendered.EveryValue(view)]);

        Assert.Contains("tracker.example", everything, StringComparison.Ordinal);
        Assert.Contains("{passkey}", everything, StringComparison.Ordinal);

        foreach (string secret in Secrets)
        {
            Assert.DoesNotContain(secret, everything, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <remarks>
    /// A failure that quotes the address it was asking for is a failure that
    /// quotes the passkey in it, and that address goes into the journal and the
    /// log. The redaction is <c>Addresses</c>', and this is the rule it exists
    /// for.
    /// </remarks>
    [Theory]
    [InlineData("https://tracker.example/rss?passkey={0}")]
    [InlineData("https://indexer.example/api?apikey={0}&q=silo")]
    [InlineData("https://tracker.example/announce?passkey={0}&info_hash=x")]
    public void AFailureNeverQuotesASecretItWasCarrying(string address)
    {
        foreach (string secret in Secrets)
        {
            string said = Addresses.Redact(new Uri(string.Format(address, secret)));

            Assert.DoesNotContain(secret, said, StringComparison.OrdinalIgnoreCase);

            // And it still says which site and what went wrong, or the
            // redaction has cost the owner the only thing the message was for.
            Assert.Contains("example", said, StringComparison.Ordinal);
        }
    }

    /// <remarks>
    /// The journal is rendered on the dashboard and kept in memory for the life
    /// of the server. Anything that reached it with a secret in it would be on
    /// screen until a restart.
    /// </remarks>
    [Fact]
    public void NothingWithASecretInItCanBeWrittenToTheJournal()
    {
        ActivityJournal journal = new();

        foreach (string secret in Secrets)
        {
            journal.Failed(ActivityStage.Find, "a tracker", Addresses.Redact(new Uri($"https://x.example/?passkey={secret}")));
        }

        string everything = string.Join(
            " ",
            journal.Snapshot().History.Select(one => $"{one.Subject} {one.Detail}"));

        foreach (string secret in Secrets)
        {
            Assert.DoesNotContain(secret, everything, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <remarks>
    /// And the log the host keeps, which on a real server is a file on disk
    /// that outlives the process and gets pasted into support threads.
    /// </remarks>
    [Fact]
    public async Task NothingWithASecretInItReachesTheLog()
    {
        using TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = Seeded();

        plugin.Initialize(context);

        foreach (string route in Routes)
        {
            await plugin.GetViewAsync(Requests.View(route), CancellationToken.None);
        }

        string log = string.Join(" ", context.Log.Lines);

        foreach (string secret in Secrets)
        {
            Assert.DoesNotContain(secret, log, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <remarks>
    /// <para>
    /// And the plugin's own log file (GitHub issue #7), which holds the Debug and Trace lines the server never
    /// sees — the lines about trackers, where an announce address carrying a passkey is said.
    /// </para>
    /// <para>
    /// A secret stored before the plugin started is known once it has started; one set on the page is known
    /// from the moment it is set. Each is handed to a line raw, as a passkey in an announce path, which no
    /// rule about parameter names can find.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task NoSecretTheStoreHoldsReachesThePluginsOwnLogFile()
    {
        using TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = Seeded();

        plugin.Initialize(context);

        // Saved, as the owner would, so the start reading the stored level cannot put it back.
        await plugin.Settings.SaveAsync(
            new Settings
            {
                IncompleteFolder = Path.GetTempPath(),
                IntakeFolder = Path.GetTempPath(),
                LogLevel = Microsoft.Extensions.Logging.LogLevel.Trace,
            },
            CancellationToken.None);

        // Started is not instant, so this asks until the start has learned what the store held — and fails by
        // timing out if it never does. Each round's lines carry their own mark, so a line the start wrote in
        // between cannot stand in for one of them.
        using CancellationTokenSource giving = new(Hang.Limit);

        for (int round = 0; !Clean(Said(plugin, round, Secrets), Secrets); round++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), giving.Token);
        }

        // And one set on the page is known from the moment it is set, with no start in between.
        const string fresh = "f00dfacecafe0123456789abcdef4242";

        await plugin.Settings.SetSecretAsync(SettingsStore.TrackerPasskey("another-tracker"), fresh, CancellationToken.None);

        IReadOnlyList<string> after = Said(plugin, -1, [fresh]);

        Assert.True(Clean(after, [fresh]), string.Join("\n", after));
        Assert.Contains("tracker.example", after[0], StringComparison.Ordinal);
    }

    /// <summary>One line per secret was written, and none of them carries one.</summary>
    private static bool Clean(IReadOnlyList<string> lines, IReadOnlyList<string> secrets)
    {
        return lines.Count == secrets.Count
               && lines.All(line => secrets.All(secret => !line.Contains(secret, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Hands each secret to a Debug line about a tracker, and reads back the lines that round wrote.</summary>
    private static IReadOnlyList<string> Said(TorrentDownloaderPlugin plugin, int round, IReadOnlyList<string> secrets)
    {
        string mark = $"round-{round}-";

        foreach (string secret in secrets)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(
                plugin.Log,
                "{Mark} announcing to {Tracker}",
                mark,
                $"https://tracker.example/{secret}/announce");
        }

        plugin.Log.Flush();

        return
        [
            .. (Directory.Exists(plugin.Log.Folder) ? Directory.GetFiles(plugin.Log.Folder) : [])
                .SelectMany(File.ReadAllLines)
                .Where(line => line.Contains(mark, StringComparison.Ordinal)),
        ];
    }

    /// <summary>Every route the plugin will render.</summary>
    private static string[] Routes =>
        [Pages.OverviewRoute, Pages.SettingsRoute, Pages.ActivityRoute, Pages.QueueRoute, "/nonsense"];

    /// <summary>
    /// The secrets themselves.
    /// </summary>
    /// <remarks>
    /// Shaped like the real thing — a passkey is thirty-two hex characters and
    /// an API key is whatever an indexer felt like — so that a search for them
    /// is a search for a value and not for the word "passkey".
    /// </remarks>
    private static string[] Secrets =>
        ["a1b2c3d4e5f60718293a4b5c6d7e8f90", "nm-indexer-key-4417-zzz"];

    /// <summary>A plugin whose protected storage has both of them in it.</summary>
    private static FakePluginContext Seeded()
    {
        FakePluginContext context = new();

        context.Secrets.SetAsync(SettingsStore.TrackerPasskey("some-tracker"), Secrets[0]).GetAwaiter().GetResult();
        context.Secrets.SetAsync("indexer:some-indexer:apikey", Secrets[1]).GetAwaiter().GetResult();

        return context;
    }
}

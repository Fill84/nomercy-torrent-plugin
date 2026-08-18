using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
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
            PluginView view = await plugin.GetViewAsync(new() { Route = route }, CancellationToken.None);

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
            await plugin.GetViewAsync(new() { Route = route }, CancellationToken.None);
        }

        string log = string.Join(" ", context.Log.Lines);

        foreach (string secret in Secrets)
        {
            Assert.DoesNotContain(secret, log, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Every route the plugin will render.</summary>
    private static string[] Routes =>
        [Pages.DashboardRoute, Pages.SettingsRoute, Pages.ShowsRoute, Pages.QueueRoute, "/nonsense"];

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

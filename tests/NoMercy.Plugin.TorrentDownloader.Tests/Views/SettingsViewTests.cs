using System.Globalization;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

public class SettingsViewTests
{
    /// <remarks>
    /// A passkey and an API key are secrets. The page has to answer "is one
    /// set?" without ever being able to answer "what is it?", so it is handed
    /// the key names that exist and never the values — there is no code path
    /// from this view to the secret store at all.
    /// </remarks>
    [Fact]
    public void AStoredPasskeyAndApiKeyRenderAsSetAndNeverAsTheirValue()
    {
        Settings settings = new();
        settings.Indexers.Add(new() { Id = "own-1", Name = "Mine", Address = "https://x/?q={query}" });
        settings.PrivateTrackers.Add(new() { Id = "trk-1", Host = "tracker.example" });

        PluginView view = SettingsView.Render(
            settings,
            [SettingsStore.IndexerApiKey("own-1"), SettingsStore.TrackerPasskey("trk-1")],
            []);

        string page = string.Join(" ", Rendered.Words(view));

        Assert.Contains("set", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2", page, StringComparison.OrdinalIgnoreCase);
        Assert.All(
            Rendered.All(view),
            component => Assert.DoesNotContain(
                "passkey=",
                string.Join(" ", component.Props.Values.Select(value => value?.ToString() ?? string.Empty)),
                StringComparison.OrdinalIgnoreCase));
    }

    /// <remarks>
    /// Every section of the page is something the owner can change. Until
    /// 21 August 2026 the whole page was text: it printed the settings and gave
    /// no way at all to set one, so a folder never chosen could never be
    /// chosen and the plugin had nowhere to download to.
    /// </remarks>
    [Theory]
    [InlineData("incompleteFolder")]
    [InlineData("intakeFolder")]
    [InlineData("cadences.cycle")]
    [InlineData("client.listenPort")]
    public void EverySettingIsOnTheFormTheOwnerCanChange(string field)
    {
        PluginView view = SettingsView.Render(new(), [], []);

        PluginComponent form = Rendered.ById(view, SettingsView.FormId);

        Assert.Equal(Ui.FormComponent, form.Component);

        // The client draws a real form for this component: it walks the fields
        // prop to make the inputs, collects them when its own button is
        // pressed, and posts them under the action carried here.
        Assert.Equal(
            "settings/edit",
            Assert.IsType<PluginActionIntent>(form.Action).Payload["method"]);

        Assert.Contains(field, Rendered.EveryValue(view).OfType<string>());
    }

    /// <remarks>
    /// <para>
    /// A folder is chosen, not typed. Both folder settings were plain text with
    /// an example path beside them, so the owner typed the path by hand and a
    /// typo was a plugin with nowhere to download to and no way to see why.
    /// </para>
    /// <para>
    /// This plugin asked the media server for the field — #33, closed on
    /// 30 August 2026 — and then went on not using it.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("incompleteFolder")]
    [InlineData("intakeFolder")]
    public void AFolderIsChosenRatherThanTyped(string field)
    {
        PluginView view = SettingsView.Render(new(), [], []);

        PluginFormField folder = Assert.Single(Every(view), one => one.Name == field);

        Assert.Equal(PluginFormFieldType.Folder, folder.Type);
    }

    /// <remarks>
    /// A field arrives holding what the setting holds. A form that opened empty
    /// would have the owner retyping every setting on the page to change one of
    /// them, and a blank left behind would clear it.
    /// </remarks>
    [Fact]
    public void EveryFieldOpensHoldingWhatTheSettingHolds()
    {
        Settings settings = new();
        settings.Client.ListenPort = 6881;

        PluginView view = SettingsView.Render(settings, [], []);

        Assert.Contains("6881", Rendered.EveryValue(view).Select(value => value?.ToString()));
    }

    /// <remarks>
    /// <para>
    /// Every field the page offers has somewhere to land, and every setting
    /// this plugin lets a page change is offered. A field the applier does not
    /// know is one the owner types into and saves and nothing happens; a
    /// setting the page never renders is one they cannot reach at all.
    /// </para>
    /// <para>
    /// Rendered at its fullest - advanced open, one private tracker - because
    /// that is what "reachable" means after <c>S12-07</c>: the expert fields
    /// are behind a switch and seeding needs a tracker to mean anything. Every
    /// one of them still has to be gettable to.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePageOffersEveryFieldThatCanBeApplied()
    {
        PluginView view = SettingsView.Render(WithAPrivateTracker(), [], [], advanced: true);

        IReadOnlyList<string> rendered = [.. Rendered.EveryValue(view).OfType<string>()];

        foreach (string field in SettingsEdit.Fields)
        {
            Assert.Contains(field, rendered);
        }
    }

    /// <remarks>
    /// These said they did nothing until 21 August 2026, and that was honest
    /// while nothing was behind them. Sprint 8 built the pipeline; nothing came
    /// back to turn the words into controls, so the plugin could not be asked
    /// to do anything at all from any page it serves.
    /// </remarks>
    [Theory]
    [InlineData("run-run", "run")]
    [InlineData("run-stop", "stop")]
    public void RunAndStopArePressableAndReachTheirEndpoints(string id, string route)
    {
        PluginView view = SettingsView.Render(new(), [], []);

        PluginComponent control = Rendered.ById(view, id);

        Assert.Equal(Ui.ButtonComponent, control.Component);
        Assert.Equal(route, Assert.IsType<PluginActionIntent>(control.Action).Payload["method"]);
    }

    /// <remarks>
    /// Dry run is gone from the page and the stored settings: it was a testing
    /// switch on an owner's own page, and <c>CycleOptions.DryRun</c> — the seam
    /// the pipeline tests decide a whole cycle through with no client behind it
    /// — stays without anything on this page ever writing it. So the Running
    /// sentence has one answer now rather than two.
    /// </remarks>
    [Fact]
    public void NothingOnThePageOffersADryRun()
    {
        PluginView view = SettingsView.Render(new(), [], []);

        Assert.DoesNotContain("dryRun", Rendered.EveryValue(view));
        Assert.DoesNotContain(
            "hands nothing",
            string.Join(" ", Rendered.Words(view)),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "downloads what it settles on",
            string.Join(" ", Rendered.Words(view)),
            StringComparison.Ordinal);
    }

    /// <remarks>
    /// <para>
    /// <strong>And it no longer says a change needs a restart.</strong> Every
    /// cadence label used to, and it was true: the host reads
    /// <c>IScheduledTaskPlugin.Jobs</c> only when a plugin is installed or
    /// enabled, so a saved cron did nothing until the server came up again.
    /// </para>
    /// <para>
    /// <c>S12-05</c> gave the plugin its own clock for exactly that reason. A
    /// saved cadence is now due by its new interval without a restart, which
    /// makes the sentence false - and a page that tells an owner to restart for
    /// nothing is a page that gets restarted for nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoCadenceClaimsAChangeNeedsARestart()
    {
        PluginView view = SettingsView.Render(new(), [], [], advanced: true);

        Assert.DoesNotContain(
            "restart",
            string.Join(" ", Rendered.Words(view)),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// The reason a save was refused belongs on the page beside the field, not
    /// in a log the owner has no reason to open.
    /// </remarks>
    [Fact]
    public void ARefusalIsShownOnThePage()
    {
        PluginView view = SettingsView.Render(
            new(),
            [],
            ["The hour is 0 to 23, and '24' is not."]);

        Assert.Contains(
            "The hour is 0 to 23, and '24' is not.",
            string.Join(" ", Rendered.Words(view)),
            StringComparison.Ordinal);
    }

    /// <remarks>
    /// The page is a form, so a client knows to give it a form's shell rather
    /// than the ordinary one.
    /// </remarks>
    [Fact]
    public void TheSettingsPageIsAForm()
    {
        Assert.Equal(PluginLayout.Wide, SettingsView.Render(new(), [], []).Layout);
    }

    /// <remarks>
    /// <para>
    /// <strong>And no page tells the owner to forward a port they forwarded
    /// months ago.</strong> This is the sentence that was on the page: "the
    /// router would not open port 51413 — forward TCP and UDP 51413 to this
    /// machine by hand." On this network 51413 has been forwarded by hand since
    /// August and neither UPnP nor NAT-PMP has ever answered, so the one notice
    /// the Settings page carried was the one thing on it that was untrue.
    /// </para>
    /// <para>
    /// It cannot come back, whatever the port's state: the view is handed a
    /// state and never a mapping result, so there is nothing left for it to
    /// draw that sentence from. Asserted for all three states, because a
    /// warning that returns for one of them is the same fault again.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(PortState.Open)]
    [InlineData(PortState.Unknown)]
    [InlineData(PortState.Shut)]
    public void NoStateOfThePortTellsTheOwnerToForwardItByHand(PortState port)
    {
        PluginView view = SettingsView.Render(new(), [], [], port);

        string page = string.Join(" ", [.. Rendered.Words(view), .. Rendered.EveryValue(view)]);

        Assert.DoesNotContain("by hand", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("would not open", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not be opened", page, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// <para>
    /// <strong>Three states, and only one of them is a warning.</strong> This
    /// page reported every failed UPnP and NAT-PMP attempt. On the owner's
    /// network neither protocol ever answers and the port is forwarded by hand,
    /// so that line was wrong every time it appeared — and a line that is
    /// always wrong teaches an owner to read past every line.
    /// </para>
    /// <para>
    /// So a mapping refusal is not a state of the port at all. It says the
    /// router would not open it <em>by itself</em>, which leaves the port
    /// exactly as unproven as it was: <em>not known yet</em>. Only a live check
    /// saying the port does not answer earns the warning — and until
    /// media-server #52 lands nothing can say that, so this state is built and
    /// nobody sets it.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(PortState.Open, PluginBadgeVariant.Success, "open")]
    [InlineData(PortState.Unknown, PluginBadgeVariant.Neutral, "not known yet")]
    [InlineData(PortState.Shut, PluginBadgeVariant.Warning, "shut")]
    public void ThePortSaysOpenShutOrNotKnownYet(PortState port, string variant, string says)
    {
        PluginView view = SettingsView.Render(new(), [], [], port);

        PluginComponent badge = Rendered.ById(view, "port-state");

        Assert.Equal(says, badge.Props["label"]);
        Assert.Equal(variant, badge.Props["variant"]);

        // Only shut warns. The other two are states rather than problems, and a
        // page that warns about all three warns about nothing.
        Assert.Equal(port is PortState.Shut, PluginBadgeVariant.Warning.Equals(badge.Props["variant"]));

        // And the number is on the page whatever the state, because the owner
        // forwarding it by hand is the one who needs it.
        Assert.Contains(
            new Settings().Client.ListenPort.ToString(CultureInfo.InvariantCulture),
            string.Join(" ", Rendered.EveryValue(view)),
            StringComparison.Ordinal);
    }

    /// <summary>The field names one section of the page posts.</summary>
    private static IReadOnlyList<string> Fields(PluginView view, string section)
    {
        PluginComponent form = Rendered.ById(view, section);

        return form.Props.GetValueOrDefault("fields") is IEnumerable<PluginFormField> fields
            ? [.. fields.Select(field => field.Name)]
            : throw new InvalidOperationException($"'{section}' is not a form with fields.");
    }

    /// <remarks>
    /// <para>
    /// <strong>One Save, for everything on the page.</strong> The owner asked
    /// for it on 12 September 2026, having seen the page with four of them on
    /// it: "ik wil ook een save knop hebben die alle instellingen doet
    /// opslaan". That reverses the decision of the same week, which was a form
    /// per section so that a bad value in one could not block another.
    /// </para>
    /// <para>
    /// <strong>What it costs, and it is a real cost.</strong> A form posts the
    /// fields it holds, so one Save means one post, and the applier refuses the
    /// whole post when any field in it is refused — nothing is saved and the
    /// page says which field it was. That is why it was split in the first
    /// place. The owner weighed a page with four Save buttons against that and
    /// chose the one button; the refusal naming its field is what makes it
    /// bearable.
    /// </para>
    /// <para>
    /// So this asserts one form and one button, and that every field the page
    /// draws is inside it — a field outside the form is a control the Save
    /// cannot reach, which is the fault this shape can have.
    /// </para>
    /// </remarks>
    [Fact]
    public void OneSaveSavesEveryRenderedField()
    {
        PluginView view = SettingsView.Render(WithAPrivateTracker(), [], [], advanced: true);

        PluginComponent[] forms =
        [
            .. Rendered.All(view).Where(one => one.Props.ContainsKey("fields")),
        ];

        PluginComponent form = Assert.Single(forms);

        Assert.Equal(SettingsView.FormId, form.Id);
        Assert.Equal("Save", form.Props.GetValueOrDefault("submitLabel"));
        Assert.Equal(SettingsView.SaveAction, form.Action!.Payload["method"]);

        // And it really holds the lot, from every group.
        IReadOnlyList<string> held = Fields(view, SettingsView.FormId);

        foreach (string field in new[]
                 {
                     "incompleteFolder",
                     "cadences.cycle",
                     "client.maxConcurrentDownloads",
                     "client.seedRatio",
                     "client.stallMinutes",
                     "client.resumeIntervalSeconds",
                 })
        {
            Assert.Contains(field, held);
        }
    }

    /// <remarks>
    /// And saving one leaves the others alone. This is the applier's half of the
    /// same promise: it is handed the keys of one form and must not touch a
    /// setting no key named.
    /// </remarks>
    [Fact]
    public void SavingOneSectionLeavesTheOthersAlone()
    {
        Settings settings = new();
        settings.Cadences.Cycle = "0 */6 * * *";
        settings.Client.MaxConcurrentDownloads = 9;

        IReadOnlyList<string> problems = SettingsEdit.Apply(
            settings,
            new Dictionary<string, string?> { ["incompleteFolder"] = Path.GetTempPath() });

        Assert.Empty(problems);
        Assert.Equal("0 */6 * * *", settings.Cadences.Cycle);
        Assert.Equal(9, settings.Client.MaxConcurrentDownloads);
    }

    /// <remarks>
    /// <para>
    /// <strong>Nothing public is ever uploaded</strong> — `docs/06-torrent-client.md`
    /// § Uploading, the owner's rule of 22 August 2026 — so seed ratio, seed
    /// hours and the upload limit decide nothing at all on an install with no
    /// private tracker. Three boxes that cannot affect anything are three boxes
    /// an owner reasonably expects to work.
    /// </para>
    /// <para>
    /// So they are not drawn, and a line says why rather than leaving the
    /// absence to be noticed. The design's section table lists "upload limit"
    /// under the client; its seeding paragraph counts it as one of the three
    /// that disappear. The paragraph is the one that agrees with the uploading
    /// rule, and this is built to the paragraph.
    /// </para>
    /// </remarks>
    [Fact]
    public void SeedingIsDrawnOnlyWhenAPrivateTrackerExists()
    {
        string[] seeding = ["client.seedRatio", "client.seedHours", "client.maxUploadRate"];

        PluginView bare = SettingsView.Render(new(), [], [], advanced: true);

        foreach (string field in seeding)
        {
            Assert.DoesNotContain(field, Fields(bare, SettingsView.FormId));
        }

        // And the absence is explained rather than left to be noticed.
        Assert.Contains(
            "private",
            string.Join(" ", Rendered.Words(bare)),
            StringComparison.OrdinalIgnoreCase);

        PluginView with = SettingsView.Render(WithAPrivateTracker(), [], [], advanced: true);

        foreach (string field in seeding)
        {
            Assert.Contains(field, Fields(with, SettingsView.FormId));
        }
    }

    /// <remarks>
    /// <para>
    /// The expert fields appear with the switch and not before it. An owner
    /// changing a folder should not have to walk past a resume interval to
    /// reach it; an owner who wants the resume interval should not have to
    /// guess that there is one.
    /// </para>
    /// <para>
    /// Asserted on whether they are drawn rather than on which form holds
    /// them: since the owner asked for one Save there is only one form, so
    /// membership says nothing and presence says everything.
    /// </para>
    /// </remarks>
    [Fact]
    public void AdvancedHoldsTheExpertFields()
    {
        string[] expert =
        [
            "client.stallMinutes",
            "client.metadataTimeoutMinutes",
            "client.encryption",
            "client.resumeIntervalSeconds",
        ];

        IReadOnlyList<string> open = Fields(
            SettingsView.Render(new(), [], [], advanced: true),
            SettingsView.FormId);

        IReadOnlyList<string> shut = Fields(
            SettingsView.Render(new(), [], [], advanced: false),
            SettingsView.FormId);

        foreach (string field in expert)
        {
            Assert.Contains(field, open);
            Assert.DoesNotContain(field, shut);
        }

        // And the ordinary settings are there either way.
        foreach (string field in new[] { "incompleteFolder", "intakeFolder", "client.maxConcurrentDownloads" })
        {
            Assert.Contains(field, open);
            Assert.Contains(field, shut);
        }
    }

    /// <remarks>
    /// <para>
    /// <strong>Show advanced writes nothing.</strong> It is a display state: the
    /// fields behind it still apply while it is closed, and turning it on saves
    /// nothing to `config.json`. A switch that quietly changed behaviour would
    /// be the worst kind of setting.
    /// </para>
    /// <para>
    /// Closed, the block is not on the page at all — so a page rendered without
    /// it has no advanced form to find, and every advanced field is absent from
    /// every other section rather than having moved into one.
    /// </para>
    /// </remarks>
    [Fact]
    public void ShowAdvancedOnlyDecidesWhatIsDrawn()
    {
        PluginView closed = SettingsView.Render(new(), [], [], advanced: false);

        // Closed, the expert fields are not on the page at all - not moved
        // into another group, not drawn disabled. There is nothing to post.
        Assert.DoesNotContain("client.stallMinutes", Fields(closed, SettingsView.FormId));
        Assert.DoesNotContain("client.resumeIntervalSeconds", Fields(closed, SettingsView.FormId));

        // The cadence stays: it is an ordinary setting drawn as an
        // interval, and only their raw expressions are expert.
        Assert.Contains("cadences.cycle", Fields(closed, SettingsView.FormId));

        // And the switch itself is a button, not a field: a field would be
        // posted and written down.
        PluginComponent toggle = Rendered.ById(closed, "advanced-toggle");

        Assert.NotNull(toggle.Action);
        Assert.Equal(SettingsView.AdvancedAction, toggle.Action!.Payload["method"]);
    }

    /// <remarks>
    /// <para>
    /// <strong>A cadence is a choice, not a syntax.</strong> The four boxes held
    /// raw cron expressions, which is a language the owner has no reason to
    /// know and one this plugin refuses on a typo — the page's own
    /// <c>AnInvalidCronIsRefusedWithTheReasonAndChangesNothing</c> exists
    /// because that happened.
    /// </para>
    /// <para>
    /// So the ordinary control is an interval from a list, whose values are the
    /// cron expressions it stands for, and the raw box stays under advanced for
    /// whoever wants it. An expression typed there is still refused with its
    /// reason.
    /// </para>
    /// </remarks>
    [Fact]
    public void ACadenceIsChosenFromAList()
    {
        PluginView view = SettingsView.Render(new(), [], [], advanced: true);

        IReadOnlyList<PluginFormField> fields = [.. Every(view).Where(field => field.Name.StartsWith("cadences.", StringComparison.Ordinal) && field.Type == PluginFormFieldType.Select)];

        Assert.Single(fields);

        foreach (PluginFormField field in fields)
        {
            Assert.Equal(PluginFormFieldType.Select, field.Type);
            Assert.NotNull(field.Options);
            Assert.NotEmpty(field.Options!);

            // Every option is a cron expression, because that is what is stored
            // and nothing in between translates.
            foreach (PluginFormOption option in field.Options!)
            {
                Assert.Equal(5, option.Value?.ToString()?.Split(' ').Length);
            }
        }

        // The raw box is advanced, and it is the same setting.
        Assert.Equal(
            1,
            Every(view).Count(field =>
                field.Name.StartsWith("cadences.", StringComparison.Ordinal)
                && field.Type != PluginFormFieldType.Select));

        // And a typed expression is still judged. By the store, which is where
        // a cron is validated - the applier only puts the text where it goes.
        Assert.Contains("cadences.cycle", Fields(view, SettingsView.FormId));
    }

    /// <summary>Settings with one private tracker, which is what makes seeding mean anything.</summary>
    private static Settings WithAPrivateTracker()
    {
        Settings settings = new();

        settings.PrivateTrackers.Add(new PrivateTracker
        {
            Id = "one",
            Host = "tracker.example",
            AnnounceTemplate = "https://tracker.example/{passkey}/announce",
        });

        return settings;
    }

    /// <summary>Every field the page draws, whatever group it sits in.</summary>
    private static IReadOnlyList<PluginFormField> Every(PluginView view)
    {
        return
        [
            .. Rendered.All(view)
                .Select(one => one.Props.GetValueOrDefault("fields"))
                .OfType<IEnumerable<PluginFormField>>()
                .SelectMany(fields => fields),
        ];
    }

    /// <remarks>
    /// <para>
    /// <strong>A list that cannot show what is stored shows nothing.</strong>
    /// The owner's own server had Transfers on an expression none of the seven
    /// intervals offers, and the page drew an empty "Select..." — so the page
    /// said the cadence was unset when it was running perfectly well, and
    /// saving from there would have written whatever the box fell back to.
    /// </para>
    /// <para>
    /// Seen on the live server on 12 September 2026, the first time this page
    /// was looked at with real settings behind it. So a stored value the list
    /// does not offer is added to it, and stays selected.
    /// </para>
    /// </remarks>
    [Fact]
    public void ACadenceKeepsAStoredExpressionTheListDoesNotOffer()
    {
        Settings settings = new();
        settings.Cadences.Cycle = "*/7 * * * *";

        PluginView view = SettingsView.Render(settings, [], []);

        PluginFormField chooser = Assert.Single(
            Every(view),
            field => field.Name == "cadences.cycle" && field.Type == PluginFormFieldType.Select);

        Assert.Equal("*/7 * * * *", chooser.Value);
        Assert.Contains("*/7 * * * *", chooser.Options.Select(option => option.Value?.ToString()));
    }

    /// <remarks>
    /// <para>
    /// <strong>A button is a button, not a bar across the page.</strong> Seen on
    /// the owner's server on 12 September 2026: Show advanced, Run now and Stop
    /// each drew as a full-width strip with the words at the far left, which
    /// reads as a section heading rather than something to press.
    /// </para>
    /// <para>
    /// The client is not at fault and the plugin cannot style anything.
    /// <c>PluginButton</c> is <c>inline-flex</c> — it asks to be exactly as wide
    /// as its words. What stretched it is the box it was put in: a page column
    /// and a <c>PluginDetail</c> body are both <c>flex-col</c>, and a flex
    /// column stretches its children across by default. A <c>PluginRow</c> is
    /// <c>flex-row items-center</c> and stretches nothing.
    /// </para>
    /// <para>
    /// So every button on this page goes in a row. That is the whole fix, and
    /// it is the plugin's to make: it chose the container.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryButtonSitsInARowRatherThanStretchingAcrossThePage()
    {
        PluginView view = SettingsView.Render(new(), [], []);

        string[] buttons = ["advanced-toggle", "run-run", "run-stop"];

        foreach (string id in buttons)
        {
            Assert.True(
                InARow(view, id),
                $"'{id}' is not inside a row, so a flex column will stretch it across the page.");
        }
    }

    /// <summary>Whether the component with this id is an item of a row.</summary>
    private static bool InARow(PluginView view, string id)
    {
        return Rendered.All(view)
            .Where(one => one.Component == Ui.RowComponent)
            .Any(row => Flatten(row.Items).Any(item => item.Id == id));
    }

    private static IEnumerable<PluginComponent> Flatten(IReadOnlyList<PluginComponent>? items)
    {
        foreach (PluginComponent item in items ?? [])
        {
            yield return item;

            foreach (PluginComponent inner in Flatten(item.Items))
            {
                yield return inner;
            }
        }
    }

    /// <remarks>
    /// <c>docs/specs/pages.md</c> § Settings: the run interval is chosen from every 15 minutes, every 30
    /// minutes, every hour, every 6 hours, every 12 hours and once a day — and the list offers nothing
    /// the save would refuse.
    /// </remarks>
    [Fact]
    public void TheRunIntervalIsChosenFromFifteenMinutesToDaily()
    {
        PluginView view = SettingsView.Render(new(), [], []);

        PluginFormField chooser = Assert.Single(
            Every(view),
            field => field.Name == "cadences.cycle" && field.Type == PluginFormFieldType.Select);

        Assert.Equal(
            ["every 15 minutes", "every 30 minutes", "every hour", "every 6 hours", "every 12 hours", "once a day, at 4am"],
            chooser.Options.Select(option => option.Label));

        foreach (PluginFormOption option in chooser.Options)
        {
            Assert.True(
                Cron.AtLeastFifteenMinutesApart(option.Value, out string? reason),
                $"'{option.Label}' is offered and would be refused: {reason}");
        }
    }

    /// <remarks>
    /// <para>
    /// <strong>Which version is running, at the foot of the page.</strong> The owner asked for it on
    /// 18 September 2026: a plugin updates from the catalogue without a restart, and there was nowhere in the
    /// plugin at all that said which copy answered — so "is the fix in?" could only be answered by watching
    /// what it did.
    /// </para>
    /// <para>
    /// From the running assembly's own identity, which is the only thing that can say. A number the page took
    /// from the catalogue, or from the manifest on disk, would say what was installed rather than what is
    /// answering, and those are the two that come apart.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheVersionThatIsAnsweringIsAtTheFootOfThePage()
    {
        PluginView view = SettingsView.Render(new(), [], []);

        PluginComponent last = Assert.IsType<PluginComponent>((view.Components ?? [])[^1]);

        string said = last.Props["value"]?.ToString() ?? string.Empty;

        Assert.Contains(PluginIdentity.Name, said, StringComparison.Ordinal);
        Assert.Contains(PluginIdentity.Version.ToString(3), said, StringComparison.Ordinal);
    }

    /// <remarks>
    /// <c>docs/specs/show-list.md</c>: the plugin's settings page holds no quality, codec or tag setting,
    /// and no English-only setting. Those are set per show and per library on the overview.
    /// </remarks>
    [Fact]
    public void ThePageHoldsNoQualityCodecTagOrLanguageSetting()
    {
        PluginView view = SettingsView.Render(new(), [], [], advanced: true);

        string[] names = [.. Every(view).Select(field => field.Name)];

        Assert.DoesNotContain(names, name => name.StartsWith("profile.", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Every(view).Select(field => field.Label),
            label => label.Contains("English", StringComparison.OrdinalIgnoreCase)
                     || label.Contains("Codec", StringComparison.OrdinalIgnoreCase)
                     || label.Contains("Resolution", StringComparison.OrdinalIgnoreCase)
                     || label.Contains("Forbidden", StringComparison.OrdinalIgnoreCase));
    }
}

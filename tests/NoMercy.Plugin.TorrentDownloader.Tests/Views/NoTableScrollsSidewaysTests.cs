using System.Globalization;

using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.PluginSdk.Abstractions;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>
/// No table scrolls sideways: a free-text cell holds a short form, and the whole text stands under the table.
/// </summary>
/// <remarks>
/// <para>
/// 15 October 2026, on beast-unit: the Sources table scrolled sideways, because the Refusal cell held
/// Torrentz2's whole refusal, address and all. The client draws a table cell on one line and widens the
/// column to the longest one, so any refusal, History reason or Activity note can do the same.
/// </para>
/// <para>
/// Every text here is one the plugin writes: a refusal is <see cref="FetchFailure"/>'s own words with the
/// sentence the Find stage adds, a note is the Names stage's "source · term · failure", and a History reason
/// is the one written while the server's encode keeps failing.
/// </para>
/// </remarks>
public sealed class NoTableScrollsSidewaysTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>What the owner read in the Refusal cell on 15 October 2026, written the way Find writes it.</summary>
    private static readonly string Torrentz2 = FetchFailure.For(
        FetchOutcome.Unreachable,
        new Uri("https://torrentz2.nz/search?q=Dark Matter S02E08"),
        "torrentz2.nz did not answer: No such host is known. (torrentz2.nz:443)")
        + " It is left out of the rest of this run.";

    private static readonly string LimeTorrents = FetchFailure.For(
        FetchOutcome.Refused,
        new Uri("https://www.limetorrents.fun/search/all/Dark-Matter-S02E08/"),
        "www.limetorrents.fun answered 403.").ToString();

    /// <summary>A refusal with no address in it, as Find writes it for a kind nothing reads.</summary>
    private const string Unread = "It answered and nothing here reads a source of kind 'html'.";

    /// <summary>What History says while the server's encode keeps failing, as Transfers writes it.</summary>
    private const string EncodeFailing =
        "the server's last attempt at the encode failed (S02E08: the server said no more than that); "
        + "it is waited on, because the server tries a failed encode again";

    /// <summary>
    /// The longest episode release name in the captures, 81 characters: TorrentGalaxy's listing in
    /// <c>lucky2-torrentgalaxy.html</c> names it as its file, with ".mkv".
    /// </summary>
    private const string LongRelease = "Lucky.S01E04.Too.Close.to.See.It.2160p.ATVP.WEB-DL.ITA.ENG.DD5.1.DV.HDR.H.265-G66";

    /// <summary>A release name from the captures (<c>sugar1-apibay.json</c>) short enough for the Downloads cell.</summary>
    private const string ShortRelease = "Farm.S02E01.HDTV.x264-FEET";

    [Fact]
    public void ALongRefusalShowsItsFirstSentenceAndStandsWholeUnderTheTable()
    {
        PluginView view = SourcesView.Render([Source("Torrentz2", Torrentz2)], Now);

        Assert.Equal(["torrentz2.nz did not answer: No such host is known…"], Cells(view, SourcesView.TableId, "refusal"));
        Assert.Equal([Torrentz2], Under(view, SourcesView.TableId));
    }

    /// <remarks>
    /// An address is the longest part of a refusal and the one that cannot be read at a glance; on its own it
    /// is enough to widen the column, so it is under the table even when the refusal is short.
    /// </remarks>
    [Fact]
    public void ARefusalsAddressIsNeverInTheCell()
    {
        string shortWithAddress = FetchFailure.For(
            FetchOutcome.Refused,
            new Uri("https://eztvx.to/search/silo"),
            "eztvx.to answered 404.").ToString();

        PluginView view = SourcesView.Render(
            [Source("Torrentz2", Torrentz2), Source("LimeTorrents", LimeTorrents), Source("EZTV", shortWithAddress)],
            Now);

        Assert.All(Cells(view, SourcesView.TableId, "refusal"), cell => Assert.DoesNotContain("https://", cell, StringComparison.Ordinal));
        Assert.Equal("eztvx.to answered 404…", Cells(view, SourcesView.TableId, "refusal")[2]);
        Assert.Contains(shortWithAddress, Under(view, SourcesView.TableId));
    }

    /// <remarks>
    /// On Linux .NET says "Name or service not known", with no full stop, so the first full stop in the
    /// refusal is at the end of "rest of this run." — after the address. The cell stops before the address
    /// all the same.
    /// </remarks>
    [Fact]
    public void ARefusalWhoseFirstSentenceRunsOnPastTheAddressStopsBeforeIt()
    {
        string linux = FetchFailure.For(
            FetchOutcome.Unreachable,
            new Uri("https://torrentz2.nz/search?q=Dark Matter S02E08"),
            "torrentz2.nz did not answer: Name or service not known (torrentz2.nz:443)")
            + " It is left out of the rest of this run.";

        PluginView view = SourcesView.Render([Source("Torrentz2", linux)], Now);

        Assert.Equal(
            ["torrentz2.nz did not answer: Name or service not known (torrentz2.nz:443)…"],
            Cells(view, SourcesView.TableId, "refusal"));
        Assert.Equal([linux], Under(view, SourcesView.TableId));
    }

    [Fact]
    public void AShortRefusalIsUnchangedAndHasNoLineUnderTheTable()
    {
        PluginView view = SourcesView.Render([Source("EZTV", Unread)], Now);

        Assert.Equal([Unread], Cells(view, SourcesView.TableId, "refusal"));
        Assert.Empty(Under(view, SourcesView.TableId));
    }

    /// <remarks>The lines are read against the rows, so they follow the rows in whatever order the table is in.</remarks>
    [Fact]
    public void TheWholeTextsStandUnderTheTableInTheTablesOrder()
    {
        SourceReport[] sources = [Source("LimeTorrents", LimeTorrents), Source("EZTV", Unread), Source("Torrentz2", Torrentz2)];

        Assert.Equal([LimeTorrents, Torrentz2], Under(SourcesView.Render(sources, Now), SourcesView.TableId));
        Assert.Equal(
            [Torrentz2, LimeTorrents],
            Under(SourcesView.Render(sources, Now, query: Asked(("sort", "source"), ("order", "desc"))), SourcesView.TableId));
    }

    /// <remarks>"rest of this run" and the episode are only in the whole refusal, not in the cell.</remarks>
    [Fact]
    public void AFilterFindsARowByAWordOnlyInTheWholeText()
    {
        SourceReport[] sources = [Source("LimeTorrents", LimeTorrents), Source("EZTV", Unread), Source("Torrentz2", Torrentz2)];

        Assert.Equal(
            ["Torrentz2"],
            Cells(SourcesView.Render(sources, Now, query: Asked(("filter", "rest of this run"))), SourcesView.TableId, "source"));
        Assert.Equal(
            ["LimeTorrents", "Torrentz2"],
            Cells(SourcesView.Render(sources, Now, query: Asked(("filter", "s02e08"))), SourcesView.TableId, "source"));
    }

    /// <remarks>
    /// The encode reason has no full stop before the cap, so the first sentence is longer than the cell
    /// holds and is cut at the last whole word that fits.
    /// </remarks>
    [Fact]
    public void ALongHistoryReasonIsCutAtAWordAndStandsWholeUnderTheTable()
    {
        PluginView view = HistoryView.Render(
            [
                new("failed", Now.AddHours(-1), "Andor S01E02", "cancelled by hand"),
                new("dispatched", Now.AddHours(-2), "Dark Matter S02E08", EncodeFailing),
            ]);

        IReadOnlyList<string> cells = Cells(view, HistoryView.TableId, "detail");

        Assert.Equal("cancelled by hand", cells[0]);
        Assert.Equal("the server's last attempt at the encode failed (S02E08: the server said no…", cells[1]);
        Assert.True(cells[1].Length <= LongCells.CellCharacters, $"{cells[1].Length} characters");
        Assert.Equal([EncodeFailing], Under(view, HistoryView.TableId));
    }

    [Fact]
    public void HistoryFiltersOnTheWholeReason()
    {
        PluginView view = HistoryView.Render(
            [
                new("failed", Now.AddHours(-1), "Andor S01E02", "cancelled by hand"),
                new("dispatched", Now.AddHours(-2), "Dark Matter S02E08", EncodeFailing),
            ],
            Asked(("filter", "tries a failed encode")));

        Assert.Equal(["Dark Matter S02E08"], Cells(view, HistoryView.TableId, "subject"));
    }

    /// <remarks>
    /// Under the Notes tab's own table, since the tab is what is drawn while it is chosen. The first sentence
    /// is 80 characters without its full stop, more than the cell holds beside the "…", so it is cut at a word.
    /// </remarks>
    [Fact]
    public void ALongNoteShowsItsFirstSentenceAndStandsWholeUnderTheNotesTable()
    {
        string failure = FetchFailure.For(
            FetchOutcome.Unreachable,
            new Uri("https://api.predb.net/feed/?q=Dark Matter S02E08"),
            "api.predb.net did not answer: No such host is known. (api.predb.net:443)").ToString();
        string note = $"PreDB · Dark Matter S02E08 · {failure}";

        PluginView view = Notes(
            new(ActivityStage.Names, "Dark Matter S02E08", Now.AddSeconds(-40), note),
            new(ActivityStage.Find, "Dark Matter S02E08", Now.AddSeconds(-20), "TorrentBay · 1 row"));

        Assert.Equal(
            ["PreDB · Dark Matter S02E08 · api.predb.net did not answer: No such host is…", "TorrentBay · 1 row"],
            Cells(view, ActivityView.NotesTableId, "what"));
        Assert.Equal([note], Under(view, ActivityView.NotesTableId));
    }

    [Fact]
    public void ShortNotesAreUnchangedAndHaveNoLineUnderTheTable()
    {
        PluginView view = Notes(
            new(ActivityStage.Names, "Silo S03E06", Now.AddSeconds(-40), "PreDB · 1 name"),
            new(ActivityStage.Find, "Andor S01E02", Now.AddSeconds(-20), "TorrentBay · 1 row"));

        Assert.Equal(["PreDB · 1 name", "TorrentBay · 1 row"], Cells(view, ActivityView.NotesTableId, "what"));
        Assert.Empty(Under(view, ActivityView.NotesTableId));
    }

    /// <remarks>
    /// The Downloads state carries the same encode reason after "encoding", in a table of nine columns that
    /// has the least room of any for it: 34 characters, the longest state the page writes itself.
    /// </remarks>
    [Fact]
    public void AnEncodeFailureInADownloadsStateIsCutAtAWordAndStandsWholeUnderTheTable()
    {
        DownloadRow encoding = new(
            new("8f2c", "magnet:?xt=urn:btih:8f2c", ShortRelease, GrabState.Dispatched),
            null)
        {
            EncodeFailure = EncodeFailing,
        };

        PluginView view = DownloadsView.Render([encoding]);

        Assert.Equal(["encoding — the server's last…"], Cells(view, DownloadsView.TableId, "state"));
        Assert.Equal([$"encoding — {EncodeFailing}"], Under(view, DownloadsView.TableId));
    }

    /// <remarks>
    /// "grabbed, not started (downloading)" is 34 characters, the longest state the page writes itself, and
    /// is drawn whole.
    /// </remarks>
    [Fact]
    public void AReleaseNameInDownloadsIsCutAtADotAndStandsWholeUnderTheTable()
    {
        PluginView view = DownloadsView.Render([Grabbed("a1", LongRelease)]);

        Assert.Equal(["Lucky.S01E04.Too.Close.to…"], Cells(view, DownloadsView.TableId, "release"));
        Assert.Equal(["grabbed, not started (downloading)"], Cells(view, DownloadsView.TableId, "state"));
        Assert.Equal([LongRelease], Under(view, DownloadsView.TableId));
    }

    [Fact]
    public void AShortReleaseNameIsUnchangedAndHasNoLineUnderTheTable()
    {
        PluginView view = DownloadsView.Render([Grabbed("a1", ShortRelease)]);

        Assert.Equal([ShortRelease], Cells(view, DownloadsView.TableId, "release"));
        Assert.Empty(Under(view, DownloadsView.TableId));
    }

    /// <remarks>
    /// The owner's decision of 15 October 2026: every download lands in the same incomplete folder, so a
    /// column naming it says nothing about the row it is on, and the ten columns did not fit at 1,600 px.
    /// </remarks>
    [Fact]
    public void TheDownloadsTableHasNoDestinationColumn()
    {
        PluginComponent table = Rendered.ById(DownloadsView.Render([Grabbed("a1", ShortRelease)]), DownloadsView.TableId);

        Assert.DoesNotContain(
            (IEnumerable<PluginTableColumn>)table.Props["columns"]!,
            column => column.Key == "destination");
        Assert.All(table.Items, row => Assert.False(row.Props.ContainsKey("destination")));
    }

    /// <remarks>
    /// A line about a release rather than an episode names the release in Which, and a release name runs to
    /// 81 characters in the captures (Lucky S01E04). It is cut at the last dot that fits.
    /// </remarks>
    [Fact]
    public void AReleaseNameInHistoryIsCutAtADotAndStandsWholeUnderTheTable()
    {
        PluginView view = HistoryView.Render([new("failed", Now.AddHours(-1), LongRelease, "cancelled by hand")]);

        Assert.Equal(["Lucky.S01E04.Too.Close.to.See.It.2160p.ATVP.WEB-DL.ITA.ENG…"], Cells(view, HistoryView.TableId, "subject"));
        Assert.Equal(["cancelled by hand"], Cells(view, HistoryView.TableId, "detail"));
        Assert.Equal([LongRelease], Under(view, HistoryView.TableId));
    }

    /// <remarks>
    /// While a release is grabbed the Now table names it twice: in Episode, as Find's "release · source", and
    /// in Waiting on, as what Grab is waiting on.
    /// </remarks>
    [Fact]
    public void AReleaseNameInTheNowTableIsCutAtADotAndStandsWholeUnderTheTable()
    {
        string asked = $"{LongRelease} · TorrentGalaxy";

        PluginView view = ActivityView.Render(
            new([new(ActivityStage.Grab, ActivityOutcome.Started, asked, Now.AddSeconds(-5), LongRelease)], [], Now),
            new(true, null, null) { StartedAt = Now.AddMinutes(-3) },
            [],
            ActivityView.NowTableId);

        Assert.Equal(["Lucky.S01E04.Too.Close.to.See.It.2160p.ATVP.WEB-DL.ITA.ENG.DD5.1.DV.HDR.H.265-G66…"], Cells(view, ActivityView.NowTableId, "subject"));
        Assert.Equal(["Lucky.S01E04.Too.Close.to.See.It.2160p.ATVP.WEB-DL.ITA.ENG.DD5.1.DV.HDR.H…"], Cells(view, ActivityView.NowTableId, "waiting"));
        Assert.Equal([asked, LongRelease], Under(view, ActivityView.NowTableId));
    }

    /// <remarks>
    /// The Pirate Bay names a release "… DDP5.1 Atmos. X265 POOTLED" (<c>the-pirate-bay.json</c>): a full
    /// stop in a release name ends nothing, so the cell keeps what comes after it.
    /// </remarks>
    [Fact]
    public void AFullStopInAReleaseNameIsNotTheEndOfIt()
    {
        const string release = "Silo S03E06 The Drive 1080P ATVP WEB-DL DDP5.1 Atmos. X265 POOTLED";

        PluginView view = HistoryView.Render([new("failed", Now.AddHours(-1), release, "cancelled by hand")]);

        Assert.Equal(["Silo S03E06 The Drive 1080P ATVP WEB-DL DDP5.1 Atmos. X265…"], Cells(view, HistoryView.TableId, "subject"));
    }

    private static DownloadRow Grabbed(string hash, string release)
    {
        return new(new(hash, $"magnet:?xt=urn:btih:{hash}", release, GrabState.Downloading), null);
    }

    private static SourceReport Source(string name, string refusal)
    {
        return new(name, Now.AddMinutes(-2), 0, refusal, TimeSpan.FromSeconds(1), null);
    }

    private static PluginView Notes(params EpisodeNote[] notes)
    {
        ActivitySnapshot running = new([], [], Now)
        {
            Run = new(Now.AddMinutes(-3), new Dictionary<RunCounter, int>()),
            Notes = notes,
        };

        return ActivityView.Render(running, new(true, null, null) { StartedAt = Now.AddMinutes(-3) }, [], ActivityView.NotesTableId);
    }

    private static TableQuery Asked(params (string Key, string Value)[] keys)
    {
        return TableQuery.From(keys.ToDictionary(key => key.Key, key => key.Value));
    }

    private static IReadOnlyList<string> Cells(PluginView page, string tableId, string key)
    {
        return [.. Rendered.ById(page, tableId).Items.Select(row => Convert.ToString(row.Props[key], CultureInfo.InvariantCulture) ?? string.Empty)];
    }

    /// <summary>The lines of text that stand straight under the table, before anything else on the page.</summary>
    private static IReadOnlyList<string> Under(PluginView page, string tableId)
    {
        return
        [
            .. (page.Components ?? [])
                .SkipWhile(component => component.Id != tableId)
                .Skip(1)
                .TakeWhile(component => component.Component == Ui.TextComponent)
                .Select(component => (string)component.Props["value"]!),
        ];
    }
}

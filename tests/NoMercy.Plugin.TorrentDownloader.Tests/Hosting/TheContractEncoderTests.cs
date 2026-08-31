using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// The encode asked for through the contract, with no reflection anywhere in
/// it.
/// </summary>
/// <remarks>
/// <para>
/// media-server #30 gives plugins <c>IPluginEncoder</c> and #35 puts the
/// server's own episode id in the library answer. Between them every line of
/// reflection in this plugin becomes unnecessary: the encode is asked for by
/// calling a method, and the episode is named by an id the server handed over
/// rather than by a row dug out of <c>MediaContext</c>.
/// </para>
/// <para>
/// Both issues were opened by this plugin, and closed on 30 August 2026.
/// </para>
/// </remarks>
public class TheContractEncoderTests
{
    private const string TelevisionLibrary = "01KZGKX2G0966V80H26EKGG5T0";

    private const int Silo = 41;

    /// <summary>The server's own id for Silo S03E06, which is not 41 and not 6.</summary>
    private const int ItsOwnId = 88214;

    [Fact]
    public async Task TheEncodeIsAskedForByNameWithTheServersOwnIdForTheEpisode()
    {
        RecordingPluginEncoder encoder = new();
        FakeProvider server = new();

        EncodeAsk ask = await Gateway(encoder, server)
            .DispatchAsync(@"D:\intake\Silo.mkv", new(Silo, 3, 6), Show(), null, CancellationToken.None);

        Assert.True(ask.Taken);

        // The job the server queued, kept so what became of it can be asked
        // rather than waited out. media-server #31.
        Assert.Equal("01KZGKX2G0966V80H26EKGG5T1", ask.JobId);

        (string File, string Library, string? Media, string? Preset) asked = Assert.Single(encoder.Asked);

        Assert.Equal(@"D:\intake\Silo.mkv", asked.File);
        Assert.Equal(TelevisionLibrary, asked.Library);

        // The id the server gave for this episode, as text. Null here is the
        // fault #35 was opened for: the server falls back to a text search on
        // whatever a parser reads out of the file name, the encode registers
        // against no row, the queue counter moves and the library stays empty.
        Assert.Equal("88214", asked.Media);

        // Null keeps the library's own presets, which is what this plugin has
        // no opinion about.
        Assert.Null(asked.Preset);
    }

    /// <remarks>
    /// A refusal says why before it returns false. The caller learns only that
    /// it was not taken and acts the same way whatever the reason — leave the
    /// file staged and ask again next tick — so an implementation that stays
    /// quiet leaves the owner with an episode that never arrives and nothing
    /// anywhere saying why.
    /// </remarks>
    [Fact]
    public async Task ARefusalIsSaidOutLoudBeforeItIsReturned()
    {
        RecordingPluginEncoder encoder = new() { Refusal = "no encoder profile for that library" };
        FakeProvider server = new();

        EncodeAsk ask = await Gateway(encoder, server)
            .DispatchAsync(@"D:\intake\Silo.mkv", new(Silo, 3, 6), Show(), null, CancellationToken.None);

        Assert.False(ask.Taken);
        Assert.Contains(
            server.Journal.Snapshot().History,
            one => one.Outcome == ActivityOutcome.Failed
                   && (one.Detail ?? string.Empty).Contains("no encoder profile", StringComparison.Ordinal));
    }

    /// <remarks>
    /// An episode the server named no id for is refused rather than asked for
    /// with none. The contract takes a null id to mean "work it out from the
    /// file name", which is precisely the guess #35 removed — and a guess made
    /// silently is an encode that appears to run and a library that stays
    /// empty. Said out loud instead.
    /// </remarks>
    [Fact]
    public async Task AnEpisodeTheServerNamedNoIdForIsRefusedRatherThanGuessedAt()
    {
        RecordingPluginEncoder encoder = new();
        FakeProvider server = new();

        // Season three, episode nine: a real episode of the show, and one this
        // library answer carries no id for.
        EncodeAsk ask = await Gateway(encoder, server)
            .DispatchAsync(@"D:\intake\Silo.mkv", new(Silo, 3, 9), Show(), null, CancellationToken.None);

        Assert.False(ask.Taken);
        Assert.Empty(encoder.Asked);
        Assert.Contains(
            server.Journal.Snapshot().History,
            one => one.Outcome == ActivityOutcome.Failed
                   && (one.Detail ?? string.Empty).Contains("id", StringComparison.OrdinalIgnoreCase));
    }

    /// <remarks>
    /// <para>
    /// A server that offers <c>IPluginEncoder</c> is asked through it, and
    /// there is no other way to ask any more: <c>EncodeDispatch</c> is deleted
    /// and with it the last reflection in this plugin.
    /// </para>
    /// <para>
    /// A server too old to offer it is told so rather than guessed at. Left to
    /// return nothing, downloads would go on finishing and staging and every
    /// one of them would wait in the intake folder for an encode that could
    /// never be asked for — which is the shape of failure this plugin exists
    /// not to have.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AServerWithoutTheContractIsToldSoRatherThanGuessedAt()
    {
        FakeProvider server = new();

        Assert.IsType<ContractEncodeGateway>(
            EncodeGateway.For(
                new Offering(new RecordingPluginEncoder()),
                new HostLibrary(new FakeLibraryQuery()),
                server.Journal,
                server.Log));

        // Nothing at all on offer, which is every server before 0.1.479.
        IEncodeGateway none = EncodeGateway.For(
            new Offering(null),
            new HostLibrary(new FakeLibraryQuery()),
            server.Journal,
            server.Log);

        EncodeAsk ask = await none.DispatchAsync(
            @"D:\intake\Silo.mkv",
            new(Silo, 3, 6),
            Show(),
            null,
            CancellationToken.None);

        Assert.False(ask.Taken);
        Assert.Contains(
            server.Journal.Snapshot().History,
            one => one.Outcome == ActivityOutcome.Failed
                   && (one.Detail ?? string.Empty).Contains("IPluginEncoder", StringComparison.Ordinal));
    }

    /// <summary>A server that offers the encoder, or one that offers nothing.</summary>
    private sealed class Offering(IPluginEncoder? encoder) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return serviceType == typeof(IPluginEncoder) ? encoder : null;
        }
    }

    private static ContractEncodeGateway Gateway(IPluginEncoder encoder, FakeProvider server)
    {
        FakeLibraryQuery query = new FakeLibraryQuery()
            .Library(TelevisionLibrary, "Television", "tv")
            .Show(Silo, "Silo", TelevisionLibrary, year: 2023)
            .Episode(Silo, 3, 6, id: ItsOwnId)

            // Named by the server and carrying no id of its own, which is what
            // an older answer looks like.
            .Episode(Silo, 3, 9, id: 0);

        return new(encoder, new HostLibrary(query), server.Journal, server.Log);
    }

    private static Show Show()
    {
        return new(Silo, "Silo", 2023, TelevisionLibrary, LibraryKind.Television, "Silo (2023)");
    }

    /// <summary>The server's encoder, which records what it was asked for.</summary>
    private sealed class RecordingPluginEncoder : IPluginEncoder
    {
        private readonly List<(string File, string Library, string? Media, string? Preset)> _asked = [];

        /// <summary>Why it will not take it, or null to take it.</summary>
        public string? Refusal { get; init; }

        public IReadOnlyList<(string File, string Library, string? Media, string? Preset)> Asked => _asked;

        public Task<PluginEncodeResult> EncodeAsync(
            string file,
            string libraryId,
            string? mediaId = null,
            string? presetId = null,
            CancellationToken ct = default)
        {
            _asked.Add((file, libraryId, mediaId, presetId));

            return Task.FromResult(
                Refusal is null
                    ? PluginEncodeResult.Queued("01KZGKX2G0966V80H26EKGG5T1")
                    : PluginEncodeResult.Refused(Refusal));
        }
    }
}

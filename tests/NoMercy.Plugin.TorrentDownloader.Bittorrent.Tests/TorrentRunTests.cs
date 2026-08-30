using System.Buffers.Binary;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// One torrent's whole life, driven.
/// </summary>
/// <remarks>
/// Sprint 5 built the trackers, the peer wire, the pieces, the disk, the
/// metadata exchange and the session, and <c>BittorrentEngine</c> — the only
/// implementation of the port the plugin calls — joined none of them: it parsed
/// a magnet, recorded the hash and stopped. This is the loop that runs them, and
/// every rule here is one without which nothing downloads.
/// </remarks>
public class TorrentRunTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nomercy-run-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// Every tracker, and the peers each names. A magnet is a hash and a list
    /// of trackers and nothing else: without the announce there is nobody to
    /// ask for the metadata, so the torrent sits saying "fetching metadata" for
    /// as long as the server runs.
    /// </remarks>
    [Fact]
    public async Task EveryTrackerIsAnnouncedToAndEveryPeerTheyNameIsDialled()
    {
        AnsweringTrackers trackers = new();
        RecordingDialler dialler = new();

        using TorrentRun run = Run(trackers, dialler);

        await run.OnceAsync(CancellationToken.None);

        Assert.Equal(
            ["http://one.example/announce", "http://two.example/announce"],
            trackers.Asked.Order());

        // One peer per tracker answer, and the same peer from two trackers is
        // one peer: dialling it twice is two connections to one client, which
        // is how a swarm of six looks like a swarm of twelve.
        PeerAddress dialled = Assert.Single(dialler.Dialled);

        Assert.Equal("192.0.2.1", dialled.Address.ToString());
        Assert.Equal(51413, dialled.Port);
    }

    /// <remarks>
    /// A tracker that will not answer costs that tracker. A torrent with six
    /// trackers where the first is down is a torrent that still has five, and
    /// stopping at the first refusal is how a swarm with hundreds of peers came
    /// to look empty.
    /// </remarks>
    [Fact]
    public async Task ATrackerThatWillNotAnswerCostsThatTrackerAndNothingElse()
    {
        AnsweringTrackers trackers = new();
        trackers.Refuse("http://one.example/announce");

        RecordingDialler dialler = new();

        using TorrentRun run = Run(trackers, dialler);

        await run.OnceAsync(CancellationToken.None);

        Assert.Single(dialler.Dialled);
    }

    /// <remarks>
    /// Nothing is dialled twice in quick succession. A peer just dialled is one
    /// the next announce names again, and re-dialling it every interval is a
    /// client that opens a connection a minute to the same machine until it is
    /// banned.
    /// </remarks>
    [Fact]
    public async Task APeerJustDialledIsNotDialledAgainOnTheNextAnnounce()
    {
        AnsweringTrackers trackers = new();
        RecordingDialler dialler = new();

        using TorrentRun run = Run(trackers, dialler);

        await run.OnceAsync(CancellationToken.None);
        await run.OnceAsync(CancellationToken.None);

        Assert.Single(dialler.Dialled);
    }

    /// <remarks>
    /// And it is offered again once that floor has passed. Every address this
    /// run dialled went into a set nothing ever took it out of, so a torrent
    /// that lost the peers of its first announce could never replace them: each
    /// later announce named the same addresses and every one of them was
    /// already in the set. Only a pause and a resume, which cleared it, brought
    /// such a torrent back.
    ///
    /// On 30 August 2026 a season pack fetched its metadata from the peers of
    /// its first announce, lost them over the following minutes, and then sat
    /// at nought peers, nought seeds and nought per cent for as long as it was
    /// left alone — while another client on the same machine saw three hundred
    /// seeds in the same swarm.
    /// </remarks>
    [Fact]
    public async Task AnAddressThatIsNoLongerConnectedIsDialledAgainOnceTheFloorHasPassed()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 30, 5, 0, 0, TimeSpan.Zero));

        AnsweringTrackers trackers = new();
        RecordingDialler dialler = new();

        using TorrentRun run = Run(trackers, dialler, time: clock);

        await run.OnceAsync(CancellationToken.None);

        // Sooner than the floor, so the address is left alone.
        clock.Advance(TorrentRun.RedialAfter - TimeSpan.FromSeconds(5));
        await run.OnceAsync(CancellationToken.None);

        Assert.Single(dialler.Dialled);

        // Past it, and without a second announce: this run has not waited the
        // tracker's half hour, and a run with nobody left to talk to cannot
        // wait that long to try somebody.
        clock.Advance(TimeSpan.FromSeconds(10));
        await run.OnceAsync(CancellationToken.None);

        Assert.Equal(2, dialler.Dialled.Count);
        Assert.Equal(["http://one.example/announce", "http://two.example/announce"], trackers.Asked.Order());
    }

    /// <remarks>
    /// A run with nobody to talk to comes round sooner than the tracker asked.
    /// The announce keeps the tracker's own interval whatever this says, so a
    /// shorter wait costs the tracker nothing — it is the dialling that has to
    /// happen oftener, because a torrent with no peers has nothing else to do
    /// and a quarter of an hour of having nothing to do is what the owner sees
    /// as nought per cent.
    /// </remarks>
    [Fact]
    public async Task ARunWithNobodyToTalkToComesRoundSoonerThanTheTrackerAsked()
    {
        AnsweringTrackers trackers = new();
        RecordingDialler dialler = new();

        using TorrentRun run = Run(trackers, dialler);

        await run.OnceAsync(CancellationToken.None);

        // Nobody answered, so there is nobody: the dialler is every address in
        // this swarm refusing, which is what most tracker addresses do.
        Assert.Equal(TorrentRun.LookAgainAfter, run.Wait);

        // And it really is sooner: the captured answer asks for a quarter of an
        // hour, so this is not the same number under another name.
        Assert.True(run.Interval > TorrentRun.LookAgainAfter);
    }

    /// <remarks>
    /// A magnet is a hash and a list of trackers. Everything else — the name,
    /// the file list, the piece length, the hashes every block is checked
    /// against — comes from a peer, and until it does there is no disk to open
    /// and nothing to ask anybody for. This is the exchange that turns one into
    /// the other.
    /// </remarks>
    [Fact]
    public async Task TheMetadataIsFetchedFromAPeerAndTheTorrentGetsItsFileList()
    {
        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(30));

        byte[] info = ArchiveInfo();
        ScriptedPeer peer = new(ArchiveHash, info);

        using TorrentRun run = Run(new AnsweringTrackers(), peer);

        await run.OnceAsync(stopping.Token);
        await peer.ServeAsync(stopping.Token);
        await run.Metadata.WaitAsync(TimeSpan.FromSeconds(10), stopping.Token);

        TorrentMetadata torrent = Assert.IsType<TorrentMetadata>(run.Torrent);

        Assert.Equal(379, torrent.PieceCount);
        Assert.Equal(198588270, torrent.TotalLength);
        Assert.NotEmpty(run.Files);

        // The trackers come from the magnet: the info dictionary has none, and
        // a client that took its word for it would announce to nobody.
        Assert.Equal(
            ["http://one.example/announce", "http://two.example/announce"],
            torrent.Trackers.Order());
    }

    /// <remarks>
    /// A peer that does not speak <c>ut_metadata</c> is asked for nothing. Most
    /// of a swarm does speak it, and a client that asked anyway would be
    /// sending a message every one of the others rejects.
    /// </remarks>
    [Fact]
    public async Task APeerThatDoesNotSpeakTheMetadataExtensionIsAskedForNothing()
    {
        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(30));

        ScriptedPeer peer = new(ArchiveHash, ArchiveInfo()) { Speaks = false };

        using TorrentRun run = Run(new AnsweringTrackers(), peer);

        await run.OnceAsync(stopping.Token);
        await peer.ServeAsync(stopping.Token);

        Assert.Equal(0, peer.Requested);
        Assert.Null(run.Torrent);
    }

    /// <remarks>
    /// The disk is opened as soon as anything knows what the torrent is, not
    /// when a peer turns up: a client with the metadata and no peers still has
    /// somewhere for the first block to go, and the files have to exist before
    /// resume can be judged against them.
    /// </remarks>
    [Fact]
    public async Task TheDiskIsOpenedUnderTheDownloadFolderAsSoonAsTheTorrentIsKnown()
    {
        TorrentMetadata torrent = TorrentMetadata.Read(Fixture("archive-multifile.torrent"));

        using TorrentRun run = Run(new AnsweringTrackers(), new RecordingDialler(), torrent);

        await run.OnceAsync(CancellationToken.None);

        Assert.All(
            torrent.Files,
            file => Assert.True(
                File.Exists(Path.Combine(_folder, torrent.Name, file.Path.Replace('/', Path.DirectorySeparatorChar))),
                $"{file.Path} was not created."));
    }

    /// <remarks>
    /// <para>
    /// A restart starts from what was verified last time. Without it every
    /// restart re-verifies the whole torrent, which for a six-gigabyte file on
    /// a spinning disk is several minutes of the server doing nothing else —
    /// and 0.3.4 did exactly that on every start.
    /// </para>
    /// <para>
    /// The resume file is a cache and is treated as one: what makes this test
    /// mean anything is that the files on disk are the same ones it claims,
    /// which is why the first run creates them.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARestartStartsFromWhatTheResumeFileSaysWasVerified()
    {
        TorrentMetadata torrent = TorrentMetadata.Read(Fixture("archive-multifile.torrent"));
        ResumeKeeper keeper = new(_folder, TimeSpan.Zero, TimeProvider.System);

        using (TorrentRun first = Run(new AnsweringTrackers(), new RecordingDialler(), torrent, keeper))
        {
            await first.OnceAsync(CancellationToken.None);

            Assert.Equal(0, first.Progress().BytesDone);
        }

        keeper.Stop([Claimed(torrent, verified: 10)]);

        using TorrentRun again = Run(new AnsweringTrackers(), new RecordingDialler(), torrent, keeper);

        await again.OnceAsync(CancellationToken.None);

        // The weight of those ten pieces, added up rather than multiplied: the
        // last piece of a torrent is short, and a client that multiplied would
        // report one bigger than it is.
        Assert.Equal(
            Enumerable.Range(0, 10).Sum(torrent.LengthOfPiece),
            again.Progress().BytesDone);
    }

    /// <remarks>
    /// The numbers a page draws come off the run itself, and a torrent nobody
    /// knows the size of has none of them. Nought bytes of nought is not
    /// nought per cent: it is a size nobody knows, and dividing by it prints
    /// something that is not a number.
    /// </remarks>
    [Fact]
    public async Task ATorrentWithNoMetadataReportsNoSizeRatherThanNought()
    {
        using TorrentRun run = Run(new AnsweringTrackers(), new RecordingDialler());

        await run.OnceAsync(CancellationToken.None);

        RunProgress progress = run.Progress();

        Assert.False(progress.HasMetadata);
        Assert.Null(progress.BytesTotal);
        Assert.Null(progress.Name);
        Assert.Equal(0, progress.DownloadRateBytesPerSecond);
    }

    /// <remarks>
    /// Read a second time in the same instant — a page redrawing on a push
    /// while the cadence is polling — the rate is the one last measured rather
    /// than a division by nought.
    /// </remarks>
    [Fact]
    public async Task ReadingTheProgressTwiceOverDoesNotMakeANumberThatIsNotOne()
    {
        TorrentMetadata torrent = TorrentMetadata.Read(Fixture("archive-multifile.torrent"));

        using TorrentRun run = Run(new AnsweringTrackers(), new RecordingDialler(), torrent);

        await run.OnceAsync(CancellationToken.None);

        Assert.Equal(run.Progress().DownloadRateBytesPerSecond, run.Progress().DownloadRateBytesPerSecond);
        Assert.Equal(0, run.Progress().DownloadRateBytesPerSecond);
    }

    /// <remarks>
    /// <para>
    /// docs/06-torrent-client.md: announce at the tracker's own interval. The
    /// captured answer asks for twenty-seven minutes and says not to come back
    /// inside thirteen, and a client that announced on a schedule of its own
    /// would be banned by the trackers it most wants.
    /// </para>
    /// <para>
    /// Before any tracker has answered there is no interval to honour, and the
    /// answer is the shipped default rather than nought — which would be an
    /// announce as fast as the loop could turn.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheNextAnnounceIsWhenTheTrackersSaidAndNotWhenThisClientPleases()
    {
        using TorrentRun run = Run(new AnsweringTrackers(), new RecordingDialler());

        Assert.Equal(TorrentRun.DefaultInterval, run.Interval);

        await run.OnceAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(1642), run.Interval);
    }

    /// <remarks>
    /// A tracker's minimum is a floor and never a suggestion: announcing inside
    /// it is what earns a ban. When one tracker wants longer between asks than
    /// another wants between announces, the longer wins.
    /// </remarks>
    [Fact]
    public async Task NoTrackersFloorIsBreachedByAnothersInterval()
    {
        AnsweringTrackers trackers = new() { MinimumSeconds = 3600 };

        using TorrentRun run = Run(trackers, new RecordingDialler());

        await run.OnceAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(3600), run.Interval);
    }

    /// <remarks>
    /// Pause keeps the pieces. A client that threw the bitfield away when the
    /// owner pressed pause would verify the whole torrent again on resume —
    /// minutes of the server doing nothing else for a six-gigabyte file, and
    /// from the page it would look exactly like starting over.
    /// </remarks>
    [Fact]
    public async Task PauseKeepsTheVerifiedPiecesAndResumeContinuesFromThem()
    {
        TorrentMetadata torrent = TorrentMetadata.Read(Fixture("archive-multifile.torrent"));
        ResumeKeeper keeper = new(_folder, TimeSpan.Zero, TimeProvider.System);

        using (TorrentRun first = Run(new AnsweringTrackers(), new RecordingDialler(), torrent, keeper))
        {
            await first.OnceAsync(CancellationToken.None);
        }

        keeper.Stop([Claimed(torrent, verified: 10)]);

        using TorrentRun run = Run(new AnsweringTrackers(), new RecordingDialler(), torrent, keeper);

        await run.OnceAsync(CancellationToken.None);

        long verified = Enumerable.Range(0, 10).Sum(torrent.LengthOfPiece);

        Assert.Equal(verified, run.Progress().BytesDone);

        run.Pause();

        Assert.True(run.Paused);
        Assert.Equal(verified, run.Progress().BytesDone);

        run.Resume();

        Assert.False(run.Paused);
        Assert.Equal(verified, run.Progress().BytesDone);
    }

    /// <remarks>
    /// A paused run dials nobody. One that kept announcing and answering peers
    /// while the owner had stopped it is not paused, whatever the page says.
    /// </remarks>
    [Fact]
    public async Task APausedRunAnnouncesToNobody()
    {
        AnsweringTrackers trackers = new();

        using TorrentRun run = Run(trackers, new RecordingDialler());

        run.Pause();

        await run.OnceAsync(CancellationToken.None);

        Assert.Empty(trackers.Asked);
    }

    /// <summary>A resume file claiming the first pieces, over the files really on disk.</summary>
    private ResumeData Claimed(TorrentMetadata torrent, int verified)
    {
        Bitfield has = new(torrent.PieceCount);

        for (int piece = 0; piece < verified; piece++)
        {
            has.Set(piece);
        }

        return new(
            torrent.InfoHash,
            has,
            Uploaded: 0,
            Downloaded: 0,
            [
                .. torrent.Files.Select(file => new ResumeFile(
                    file.Path,
                    file.Length,
                    new FileInfo(Path.Combine(
                        _folder,
                        torrent.Name,
                        file.Path.Replace('/', Path.DirectorySeparatorChar))).LastWriteTimeUtc)),
            ]);
    }

    private TorrentRun Run(
        AnsweringTrackers transport,
        IPeerDialler dialler,
        TorrentMetadata? torrent = null,
        ResumeKeeper? resume = null,
        TimeProvider? time = null)
    {
        TimeProvider clock = time ?? TimeProvider.System;

        return new(
            ArchiveHash,
            ["http://one.example/announce", "http://two.example/announce"],
            _folder,
            new TrackerSet(transport, clock),
            dialler,
            Id("NM0001"),
            listenPort: 51413,
            clock,
            torrent,
            resume);
    }

    /// <summary>The archive torrent's own info hash, which all of this comes back to.</summary>
    private static byte[] ArchiveHash =>
        Convert.FromHexString(TorrentMetadata.Read(Fixture("archive-multifile.torrent")).InfoHash);

    /// <summary>The raw info dictionary out of the real torrent.</summary>
    private static byte[] ArchiveInfo()
    {
        byte[] torrent = Fixture("archive-multifile.torrent");
        BencodeDocument document = Bencode.Read(torrent);

        return torrent[document.InfoStart!.Value..(document.InfoStart.Value + document.InfoLength!.Value)];
    }

    private static byte[] Id(string name)
    {
        byte[] id = new byte[20];

        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(id, 0);

        return id;
    }

    /// <summary>Trackers that answer with a real captured announce.</summary>
    private sealed class AnsweringTrackers : ITrackerTransport
    {
        private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _lock = new();
        private readonly List<string> _asked = [];

        /// <summary>
        /// A floor of its own, when a test wants one.
        /// </summary>
        /// <remarks>
        /// Nought means the captured answer's own, which is eight hundred and
        /// twenty-one seconds.
        /// </remarks>
        public int MinimumSeconds { get; init; }

        /// <summary>Every tracker address that was really fetched.</summary>
        public IReadOnlyList<string> Asked
        {
            get
            {
                lock (_lock)
                {
                    return [.. _asked];
                }
            }
        }

        public void Refuse(string tracker)
        {
            _refused.Add(tracker);
        }

        public Task<byte[]> GetAsync(Uri address, CancellationToken ct)
        {
            string tracker = address.GetLeftPart(UriPartial.Path);

            lock (_lock)
            {
                _asked.Add(tracker);
            }

            if (_refused.Contains(tracker))
            {
                throw new HttpRequestException("nothing answered");
            }

            byte[] answer = Fixture("tracker-http-announce.bin");

            // The captured answer with its floor rewritten, so the rule about
            // whose floor wins is judged against a real tracker response rather
            // than one written here.
            return Task.FromResult(MinimumSeconds == 0
                ? answer
                : Rewritten(answer, MinimumSeconds));
        }

        /// <summary>The same answer with a different <c>min interval</c>.</summary>
        private static byte[] Rewritten(byte[] answer, int seconds)
        {
            BencodeDictionary root = (BencodeDictionary)Bencode.Read(answer).Root;

            return Bencode.Write(new BencodeDictionary(
            [
                .. root.Entries.Where(entry =>
                    !"min interval"u8.SequenceEqual(entry.Key) && !"min_interval"u8.SequenceEqual(entry.Key)),
                new("min interval"u8.ToArray(), new BencodeInteger(seconds)),
            ]));
        }

        public Task<byte[]> ExchangeAsync(string host, int port, byte[] datagram, TimeSpan patience, CancellationToken ct)
        {
            int action = BinaryPrimitives.ReadInt32BigEndian(datagram.AsSpan(8));

            return Task.FromResult(Fixture(action == 0 ? "tracker-udp-connect.bin" : "tracker-udp-announce.bin"));
        }
    }

    /// <summary>A dialler that records who it was asked for.</summary>
    private sealed class RecordingDialler : IPeerDialler
    {
        private readonly Lock _lock = new();
        private readonly List<PeerAddress> _dialled = [];

        public IReadOnlyList<PeerAddress> Dialled
        {
            get
            {
                lock (_lock)
                {
                    return [.. _dialled];
                }
            }
        }

        public Task<PeerConnection?> DialAsync(
            PeerAddress peer,
            byte[] infoHash,
            byte[] peerId,
            int pieces,
            CancellationToken ct)
        {
            lock (_lock)
            {
                _dialled.Add(peer);
            }

            // Null is a peer that would not talk, which is most of the
            // addresses a tracker gives out.
            return Task.FromResult<PeerConnection?>(null);
        }
    }

    /// <summary>
    /// A peer on the other end of a pipe, answering as a real one does.
    /// </summary>
    /// <remarks>
    /// It serves the metadata and nothing else: what is being judged is whether
    /// this client asks the right questions and makes a torrent out of the
    /// answers, and everything below that is proved elsewhere.
    /// </remarks>
    private sealed class ScriptedPeer(byte[] infoHash, byte[] info) : IPeerDialler
    {
        private readonly PeerWire _wire = new();
        private PeerConnection? _theirs;

        /// <summary>The id it wants its metadata messages under, which is not ours.</summary>
        private const int TheirId = 3;

        /// <summary>Whether it offers <c>ut_metadata</c> at all.</summary>
        public bool Speaks { get; init; } = true;

        /// <summary>How many pieces of the metadata it was asked for.</summary>
        public int Requested { get; private set; }

        public async Task<PeerConnection?> DialAsync(
            PeerAddress peer,
            byte[] hash,
            byte[] peerId,
            int pieces,
            CancellationToken ct)
        {
            // Both at once. The answering side reads before it writes, so
            // waiting for it before the dialling side has said anything is a
            // deadlock — which is what a real client does to itself if it
            // accepts connections on the thread that dials.
            Task<PeerConnection?> answering = PeerConnection.IntroduceAsync(
                _wire.Receiver, infoHash, Id("PEER00"), pieces: 0, dialling: false, ct);

            Task<PeerConnection?> dialling = PeerConnection.IntroduceAsync(
                _wire.Initiator, hash, peerId, pieces, dialling: true, ct);

            PeerConnection?[] both = await Task.WhenAll(answering, dialling);

            _theirs = both[0];

            return both[1];
        }

        /// <summary>Answers whatever this client asks, until it has asked for everything.</summary>
        public async Task ServeAsync(CancellationToken ct)
        {
            PeerConnection theirs = _theirs ?? throw new InvalidOperationException("nobody dialled");

            await theirs.SendAsync(Introduction(), ct).ConfigureAwait(false);

            if (!Speaks)
            {
                // It offered nothing, so it has nothing to serve — but it keeps
                // listening until the client drops it, counting anything it is
                // asked for anyway. Without that, "it was asked for nothing"
                // would be true of a peer that was never listening.
                await ListenAsync(theirs, ct).ConfigureAwait(false);

                return;
            }

            int served = 0;
            int pieces = MetadataTransfer.Pieces(info.Length);

            while (served < pieces && !ct.IsCancellationRequested)
            {
                PeerMessage? message = await theirs.NextAsync(ct).ConfigureAwait(false);

                if (message is null)
                {
                    return;
                }

                if (message.Id != PeerMessageId.Extended
                    || message.Payload.Length < 1
                    || message.Payload[0] != TheirId)
                {
                    continue;
                }

                MetadataPart part = MetadataTransfer.Read(message);

                if (part.Kind != MetadataMessage.Request)
                {
                    continue;
                }

                Requested++;

                await theirs
                    .SendAsync(
                        MetadataTransfer.Data(Extensions.OurMetadataId, part.Piece, info.Length, Slice(part.Piece)),
                        ct)
                    .ConfigureAwait(false);

                served++;
            }
        }

        /// <summary>Reads until the client hangs up, counting what it was asked for.</summary>
        private async Task ListenAsync(PeerConnection theirs, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                PeerMessage? message = await theirs.NextAsync(ct).ConfigureAwait(false);

                if (message is null)
                {
                    return;
                }

                // Any extended message that is not the handshake, whatever id
                // it carries. "It was asked for nothing" has to mean nothing at
                // all: a client that fell back to sending requests under its
                // own id has still asked a peer that offered nothing.
                if (message.Id == PeerMessageId.Extended
                    && message.Payload.Length > 0
                    && message.Payload[0] != Extensions.HandshakeId)
                {
                    Requested++;
                }
            }
        }

        /// <summary>
        /// This peer's extension handshake, written by hand.
        /// </summary>
        /// <remarks>
        /// Under an id of its own choosing and not ours, because that is the
        /// whole point of the handshake: a client that sent its requests under
        /// its own id would be understood by nobody. Three rather than one, so
        /// a client that confused the two would be caught here.
        /// </remarks>
        private PeerMessage Introduction()
        {
            List<BencodeEntry> entries =
            [
                new(
                    "m"u8.ToArray(),
                    new BencodeDictionary(Speaks
                        ?
                        [
                            new(
                                System.Text.Encoding.ASCII.GetBytes(Extensions.Metadata),
                                new BencodeInteger(TheirId)),
                        ]
                        : [])),
            ];

            // The size either way. A peer that says how big the metadata is and
            // offers no id to ask for it is a real case — it has dropped the
            // extension since it last spoke — and it is the case that tells
            // "no id" apart from "no size".
            entries.Add(new("metadata_size"u8.ToArray(), new BencodeInteger(info.Length)));

            return Extensions.Extended(Extensions.HandshakeId, new BencodeDictionary(entries));
        }

        /// <summary>One sixteen-kibibyte piece of the metadata, the last one short.</summary>
        private byte[] Slice(int piece)
        {
            int at = piece * MetadataTransfer.PieceLength;

            return info[at..Math.Min(at + MetadataTransfer.PieceLength, info.Length)];
        }
    }

    private static byte[] Fixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllBytes(Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("no solution folder above the test assembly"),
            "tests",
            "fixtures",
            name));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}

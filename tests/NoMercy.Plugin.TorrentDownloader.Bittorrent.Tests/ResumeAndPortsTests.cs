using System.Net;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// Fast resume, stalls and getting a port opened.
/// </summary>
/// <remarks>
/// <para>
/// The resume half is put to the real Archive torrent, because what makes it
/// hard is real: twenty-three files in one byte stream, where a piece belongs
/// to two files at once and touching either of them makes that piece suspect.
/// </para>
/// <para>
/// The port-mapping half is the protocols written out and read back. It could
/// not be captured and the reason is measured rather than assumed: nothing on
/// this network answers either protocol — no device at all answered an SSDP
/// search, for any target, and the gateway at 192.168.178.1 did not answer
/// NAT-PMP. That is also why no peer has ever been able to dial this machine.
/// </para>
/// </remarks>
public class ResumeAndPortsTests
{
    /// <remarks>
    /// Everything the document names: the hash, the bitfield of verified
    /// pieces, the bytes each way, and each file's size and modification time.
    /// </remarks>
    [Fact]
    public void ResumeIsWrittenAndReadBackWhole()
    {
        TorrentMetadata torrent = Archive();
        Bitfield verified = new(torrent.PieceCount);

        verified.Set(0);
        verified.Set(3);
        verified.Set(torrent.PieceCount - 1);

        ResumeData written = new(
            torrent.InfoHash,
            verified,
            Uploaded: 4_294_967_296,
            Downloaded: 8_589_934_592,
            [.. torrent.Files.Select(one => new ResumeFile(one.Path, one.Length, Modified))]);

        ResumeData read = Assert.IsType<ResumeData>(ResumeData.Read(written.Write()));

        Assert.Equal(torrent.InfoHash, read.InfoHash);
        Assert.Equal(4_294_967_296, read.Uploaded);
        Assert.Equal(8_589_934_592, read.Downloaded);
        Assert.Equal(torrent.PieceCount, read.Verified.Pieces);

        Assert.True(read.Verified.Has(0));
        Assert.True(read.Verified.Has(3));
        Assert.True(read.Verified.Has(torrent.PieceCount - 1));
        Assert.False(read.Verified.Has(1));

        Assert.Equal(torrent.Files.Count, read.Files.Count);
        Assert.Equal(torrent.Files[0].Path, read.Files[0].Path);
        Assert.Equal(torrent.Files[0].Length, read.Files[0].Length);
        Assert.Equal(Modified, read.Files[0].ModifiedUtc);
    }

    /// <remarks>
    /// A crash mid-write leaves a file that is not bencode, or is bencode with
    /// half a bitfield in it. Either way the answer is to verify the torrent
    /// rather than to fail to start: this is a cache of what was true, not a
    /// record of anything that cannot be worked out again.
    /// </remarks>
    [Fact]
    public void AHalfWrittenResumeFileIsNotBelievedAndDoesNotThrow()
    {
        Assert.Null(ResumeData.Read("this is not bencode"u8));
        Assert.Null(ResumeData.Read(""u8));

        byte[] whole = Filled().Write();

        // Cut in half, which is what a power cut during a write leaves behind.
        Assert.Null(ResumeData.Read(whole.AsSpan(0, whole.Length / 2)));

        // And a bitfield that is not the length it says it is.
        Assert.Null(ResumeData.Read(Bencode.Write(new BencodeDictionary(
        [
            new("info_hash"u8.ToArray(), new BencodeBytes("ABC"u8.ToArray())),
            new("pieces"u8.ToArray(), new BencodeInteger(800)),
            new("bitfield"u8.ToArray(), new BencodeBytes([1, 2, 3])),
        ]))));
    }

    /// <remarks>
    /// Nothing has been touched, so nothing is verified again. A restart that
    /// re-hashed a finished six-gigabyte torrent would have the server doing
    /// nothing else for minutes, every time.
    /// </remarks>
    [Fact]
    public void AResumeWhoseFilesAreUnchangedIsBelievedWhole()
    {
        TorrentMetadata torrent = Archive();
        ResumeData resume = Filled();

        Bitfield trusted = resume.Trust(torrent, OnDisk(torrent, changed: null));

        Assert.Equal(torrent.PieceCount, trusted.Count);
    }

    /// <remarks>
    /// <para>
    /// Through the file, not around it. A resume file records a modification
    /// time in whole seconds and a real file's is not a whole number of them,
    /// so compared exactly every file looks touched the moment the resume has
    /// been written and read back — nothing is ever believed, and every restart
    /// verifies the whole torrent.
    /// </para>
    /// <para>
    /// That is the fault this class exists to prevent, and it survived because
    /// every other test here judges a <c>ResumeData</c> built in memory that
    /// has never been through <c>Write</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void AResumeThatHasBeenWrittenAndReadBackStillBelievesItsFiles()
    {
        TorrentMetadata torrent = Archive();

        // What a real file's timestamp looks like: seconds and a fraction.
        DateTimeOffset touched = new DateTimeOffset(2026, 8, 19, 9, 30, 0, TimeSpan.Zero)
                                 + TimeSpan.FromMilliseconds(437);

        Bitfield everything = new(torrent.PieceCount);

        for (int piece = 0; piece < torrent.PieceCount; piece++)
        {
            everything.Set(piece);
        }

        ResumeData written = new(
            torrent.InfoHash,
            everything,
            Uploaded: 0,
            Downloaded: torrent.TotalLength,
            [.. torrent.Files.Select(one => new ResumeFile(one.Path, one.Length, touched))]);

        ResumeData read = Assert.IsType<ResumeData>(ResumeData.Read(written.Write()));

        Bitfield trusted = read.Trust(
            torrent,
            torrent.Files.ToDictionary(one => one.Path, one => new ResumeFile(one.Path, one.Length, touched)));

        Assert.Equal(torrent.PieceCount, trusted.Count);
    }

    /// <remarks>
    /// <para>
    /// A file that has changed size, or been written to since, has been touched
    /// by something that is not this client — and every piece covering any part
    /// of it goes back to unverified.
    /// </para>
    /// <para>
    /// Including the pieces it shares with the file either side of it, which is
    /// the part worth testing on a real torrent: in the Archive torrent the
    /// first piece covers the end of a thumbnail and the start of a
    /// nine-megabyte scan, so touching the thumbnail costs the scan its first
    /// piece too.
    /// </para>
    /// </remarks>
    [Fact]
    public void AFileWhoseSizeOrTimestampChangedTakesEveryPieceItTouchesWithIt()
    {
        TorrentMetadata torrent = Archive();
        ResumeData resume = Filled();

        // The largest file in the torrent, deliberately: it spans many pieces,
        // and a client that dropped only the first of them would keep the rest
        // of a file something else has rewritten. The small files either side
        // of it span one piece each, where that fault is invisible.
        TorrentFileEntry first = torrent.Files.OrderByDescending(one => one.Length).First();
        TorrentFileEntry second = torrent.Files[torrent.Files.ToList().IndexOf(first) + 1];

        int[] firstPieces = [.. ResumeData.Covering(torrent, first)];
        int[] secondPieces = [.. ResumeData.Covering(torrent, second)];

        Assert.InRange(firstPieces.Length, 2, int.MaxValue);

        // And it shares its last piece with the file after it, which is what
        // makes the neighbour assertion below worth making.
        Assert.Contains(firstPieces, one => secondPieces.Contains(one));

        Bitfield resized = resume.Trust(torrent, OnDisk(torrent, changed: first.Path, length: first.Length - 1));

        Assert.All(firstPieces, piece => Assert.False(resized.Has(piece)));

        // And the second file keeps everything except the piece it shared.
        Assert.All(
            secondPieces.Except(firstPieces),
            piece => Assert.True(resized.Has(piece)));

        // A timestamp is enough on its own: a file rewritten with the same
        // length by something else is a file with different bytes in it.
        Bitfield touched = resume.Trust(
            torrent,
            OnDisk(torrent, changed: first.Path, modified: Modified.AddSeconds(1)));

        Assert.All(firstPieces, piece => Assert.False(touched.Has(piece)));
    }

    /// <remarks>
    /// A file that is not there at all — deleted, or on a drive that did not
    /// come back — is not a file whose pieces can be believed.
    /// </remarks>
    [Fact]
    public void AFileThatIsNoLongerThereTakesItsPiecesWithIt()
    {
        TorrentMetadata torrent = Archive();

        Dictionary<string, ResumeFile> onDisk = OnDisk(torrent, changed: null);

        onDisk.Remove(torrent.Files[5].Path);

        Bitfield trusted = Filled().Trust(torrent, onDisk);

        Assert.All(ResumeData.Covering(torrent, torrent.Files[5]), piece => Assert.False(trusted.Has(piece)));
        Assert.NotEqual(torrent.PieceCount, trusted.Count);
    }

    /// <remarks>
    /// No progress <strong>and</strong> no peers, for the whole of
    /// <c>StallMinutes</c>. Calling a healthy torrent stalled is expensive: the
    /// hash is blacklisted and the episode goes back to missing, so the plugin
    /// then refuses the very release it was downloading.
    /// </remarks>
    [Fact]
    public void NoProgressAndNoPeersForTheWholeLimitIsAStall()
    {
        FakeTimeProvider clock = new(Start);
        StallWatch watch = new(TimeSpan.FromMinutes(30), clock);

        Assert.False(watch.Observe(bytesDone: 1000, peers: 0));

        clock.Advance(TimeSpan.FromMinutes(29));

        Assert.False(watch.Observe(bytesDone: 1000, peers: 0));

        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.True(watch.Observe(bytesDone: 1000, peers: 0));
    }

    /// <remarks>
    /// Progress with no peers is not a stall — it is a torrent downloading from
    /// a peer the tracker has stopped counting — and peers with no progress is
    /// not either, which is what the endgame looks like from outside.
    /// </remarks>
    [Fact]
    public void ProgressWithoutPeersIsNotAStallAndNorArePeersWithoutProgress()
    {
        FakeTimeProvider clock = new(Start);

        StallWatch downloading = new(TimeSpan.FromMinutes(30), clock);
        StallWatch waiting = new(TimeSpan.FromMinutes(30), clock);

        long bytes = 0;

        for (int hour = 0; hour < 4; hour++)
        {
            clock.Advance(TimeSpan.FromMinutes(45));

            Assert.False(downloading.Observe(bytes += 1000, peers: 0));
            Assert.False(waiting.Observe(bytesDone: 5000, peers: 12));
        }
    }

    /// <remarks>
    /// And the clock starts again the moment either half comes back. A torrent
    /// that was quiet for twenty-nine minutes and then moved is not most of the
    /// way to being stalled; it is downloading.
    /// </remarks>
    [Fact]
    public void OneByteOrOnePeerStartsTheClockAgain()
    {
        FakeTimeProvider clock = new(Start);
        StallWatch watch = new(TimeSpan.FromMinutes(30), clock);

        watch.Observe(bytesDone: 1000, peers: 0);

        clock.Advance(TimeSpan.FromMinutes(29));

        Assert.False(watch.Observe(bytesDone: 1001, peers: 0));
        Assert.Null(watch.StuckSince);

        clock.Advance(TimeSpan.FromMinutes(29));

        Assert.False(watch.Observe(bytesDone: 1001, peers: 0));

        clock.Advance(TimeSpan.FromMinutes(29));

        Assert.False(watch.Observe(bytesDone: 1001, peers: 0));
    }

    /// <remarks>
    /// UPnP first because most routers speak it, NAT-PMP second, and the first
    /// that works stops the rest.
    /// </remarks>
    [Fact]
    public async Task UpnpIsTriedFirstAndNatPmpIsTheFallback()
    {
        FakeMapper upnp = new("UPnP", MappedBy.Upnp, works: true);
        FakeMapper natPmp = new("NAT-PMP", MappedBy.NatPmp, works: true);

        PortMapping both = new([upnp, natPmp]);

        Assert.Equal(MappedBy.Upnp, (await both.MapAsync(51413, CancellationToken.None)).By);
        Assert.Equal(1, upnp.Asked);
        Assert.Equal(0, natPmp.Asked);

        // And when the router has no UPnP, the second one is asked.
        FakeMapper refusing = new("UPnP", MappedBy.Upnp, works: false);
        PortMapping fallback = new([refusing, natPmp]);

        Assert.Equal(MappedBy.NatPmp, (await fallback.MapAsync(51413, CancellationToken.None)).By);
        Assert.Equal(1, natPmp.Asked);
    }

    /// <remarks>
    /// Neither worked, and the client carries on: a server behind a router that
    /// refuses both still downloads from peers it dials out to, and taking the
    /// plugin down over it would cost the owner everything else it does. What
    /// the page gets is <em>both</em> reasons — an owner needs to know whether
    /// the router said no or said nothing at all.
    /// </remarks>
    [Fact]
    public async Task AFailureNamesEveryReasonAndDoesNotThrow()
    {
        PortMapping nothing = new(
        [
            new FakeMapper("UPnP", MappedBy.Upnp, works: false, reason: "no device answered the search"),
            new FakeMapper("NAT-PMP", MappedBy.NatPmp, works: false, reason: "the gateway did not answer"),
        ]);

        PortMapResult result = await nothing.MapAsync(51413, CancellationToken.None);

        Assert.False(result.Mapped);
        Assert.Equal(51413, result.Port);
        Assert.Contains("UPnP: no device answered the search", result.Reason!, StringComparison.Ordinal);
        Assert.Contains("NAT-PMP: the gateway did not answer", result.Reason!, StringComparison.Ordinal);

        // And it is kept, because the Settings page says it and the owner
        // forwards the port by hand.
        Assert.Equal(result, nothing.Last);
    }

    /// <remarks>
    /// Twelve bytes: version, operation, two reserved, the port twice, and how
    /// long the mapping should last. A lifetime of nought would leave a
    /// stranger's port open on the owner's router after this plugin was
    /// uninstalled.
    /// </remarks>
    [Fact]
    public void ANatPmpRequestIsTwelveBytesAndAsksForALease()
    {
        byte[] request = NatPmp.Write(51413, NatPmp.Lifetime);

        Assert.Equal(12, request.Length);
        Assert.Equal(NatPmp.Version, request[0]);
        Assert.Equal(NatPmp.MapTcp, request[1]);
        Assert.Equal(0, request[2]);
        Assert.Equal(0, request[3]);

        Assert.Equal(51413, (request[4] << 8) | request[5]);
        Assert.Equal(51413, (request[6] << 8) | request[7]);
        Assert.Equal(7200, (request[8] << 24) | (request[9] << 16) | (request[10] << 8) | request[11]);
    }

    /// <remarks>
    /// The answer's op code comes back with the top bit set, and the result
    /// code is what says yes or no. A client that read the port out of a
    /// refusal would tell every tracker a port nothing is listening on.
    /// </remarks>
    [Fact]
    public void ANatPmpAnswerIsReadAndARefusalIsNotMistakenForOne()
    {
        byte[] yes = new byte[16];

        yes[1] = NatPmp.MapTcp + 128;
        yes[10] = 0xC8;
        yes[11] = 0xD5;

        PortMapResult mapped = NatPmp.Read(yes, 51413);

        Assert.Equal(MappedBy.NatPmp, mapped.By);
        Assert.Equal(51413, mapped.Port);

        byte[] no = new byte[16];

        no[1] = NatPmp.MapTcp + 128;
        no[3] = 2;

        PortMapResult refused = NatPmp.Read(no, 51413);

        Assert.False(refused.Mapped);
        Assert.Contains("refuses to map ports", refused.Reason!, StringComparison.Ordinal);

        // Too short, and about something else entirely.
        Assert.False(NatPmp.Read(new byte[4], 51413).Mapped);
        Assert.False(NatPmp.Read(new byte[16], 51413).Mapped);
    }

    /// <remarks>
    /// The search, and finding where the description lives in the answer.
    /// Routers spell the header every way there is, so it is matched without
    /// regard to case.
    /// </remarks>
    [Fact]
    public void TheSearchIsWrittenAndTheDescriptionIsFoundInAnAnswer()
    {
        string search = System.Text.Encoding.ASCII.GetString(Upnp.Search(Upnp.Gateways[0]));

        Assert.StartsWith("M-SEARCH * HTTP/1.1\r\n", search, StringComparison.Ordinal);
        Assert.Contains("HOST: 239.255.255.250:1900\r\n", search, StringComparison.Ordinal);
        Assert.Contains("MAN: \"ssdp:discover\"\r\n", search, StringComparison.Ordinal);
        Assert.Contains("ST: urn:schemas-upnp-org:service:WANIPConnection:1\r\n", search, StringComparison.Ordinal);

        Assert.Equal(
            "http://192.168.1.1:5000/rootDesc.xml",
            Upnp.Location(
                "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nlocation: http://192.168.1.1:5000/rootDesc.xml\r\n\r\n"u8));

        Assert.Null(Upnp.Location("HTTP/1.1 200 OK\r\nSERVER: something\r\n\r\n"u8));
    }

    /// <remarks>
    /// A real description carries a dozen services and several vendor
    /// extensions. What is wanted is the control address of the one gateway
    /// service among them, and a reader that insisted on the shape of the whole
    /// document would refuse a router that works.
    /// </remarks>
    [Fact]
    public void TheControlAddressIsFoundAmongEverythingElseADeviceOffers()
    {
        string description =
            "<root><device><serviceList>"
            + "<service><serviceType>urn:schemas-upnp-org:service:Layer3Forwarding:1</serviceType>"
            + "<controlURL>/wrong</controlURL></service>"
            + "<service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>"
            + "<controlURL>/ctl/IPConn</controlURL></service>"
            + "</serviceList></device></root>";

        Assert.Equal("/ctl/IPConn", Upnp.ControlAddress(description, out string? service));
        Assert.Equal(Upnp.Gateways[0], service);

        // A device with neither gateway service is not a gateway.
        Assert.Null(Upnp.ControlAddress("<root><device></device></root>", out string? none));
        Assert.Null(none);
    }

    /// <remarks>
    /// The call itself. Every field the router needs, the description the owner
    /// will see in its list of mappings, and a lease so that nothing is left
    /// open for ever.
    /// </remarks>
    [Fact]
    public void TheSoapCallAsksForThePortWithALeaseAndANameTheOwnerWillRecognise()
    {
        string body = Upnp.AddPortMapping(
            Upnp.Gateways[0],
            51413,
            IPAddress.Parse("192.168.1.50"),
            TimeSpan.FromHours(2));

        Assert.Contains("<u:AddPortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">", body, StringComparison.Ordinal);
        Assert.Contains("<NewExternalPort>51413</NewExternalPort>", body, StringComparison.Ordinal);
        Assert.Contains("<NewInternalPort>51413</NewInternalPort>", body, StringComparison.Ordinal);
        Assert.Contains("<NewInternalClient>192.168.1.50</NewInternalClient>", body, StringComparison.Ordinal);
        Assert.Contains("<NewProtocol>TCP</NewProtocol>", body, StringComparison.Ordinal);
        Assert.Contains("<NewLeaseDuration>7200</NewLeaseDuration>", body, StringComparison.Ordinal);
        Assert.Contains("NoMercy", body, StringComparison.Ordinal);

        Assert.Equal(
            "\"urn:schemas-upnp-org:service:WANIPConnection:1#AddPortMapping\"",
            Upnp.Action(Upnp.Gateways[0], "AddPortMapping"));

        // And the one that closes it again names the port and nothing else.
        Assert.Contains("<u:DeletePortMapping", Upnp.DeletePortMapping(Upnp.Gateways[0], 51413), StringComparison.Ordinal);
    }

    /// <remarks>
    /// A UPnP fault is HTTP 500 with the reason in the body, so the status says
    /// nothing useful on its own. 718 — another device already has that port —
    /// is the one an owner can act on, and 725 is worth saying in words because
    /// the answer to it is to ask again without a lease.
    /// </remarks>
    [Fact]
    public void ARouterRefusalIsReadOutOfTheBodyAndSaidInWords()
    {
        Assert.Contains(
            "another device already has that port",
            Upnp.Refusal("<s:Fault><detail><UPnPError><errorCode>718</errorCode></UPnPError></detail></s:Fault>")!,
            StringComparison.Ordinal);

        Assert.Contains(
            "only does permanent mappings",
            Upnp.Refusal("<UPnPError><errorCode>725</errorCode></UPnPError>")!,
            StringComparison.Ordinal);

        Assert.Contains("error 601", Upnp.Refusal("<UPnPError><errorCode>601</errorCode></UPnPError>")!, StringComparison.Ordinal);

        // A yes has no error in it at all.
        Assert.Null(Upnp.Refusal("<s:Envelope><s:Body><u:AddPortMappingResponse /></s:Body></s:Envelope>"));
    }

    private static DateTimeOffset Start => new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset Modified => new(2026, 8, 1, 9, 30, 0, TimeSpan.Zero);

    /// <summary>A resume with every piece verified and every file as it is on disk.</summary>
    private static ResumeData Filled()
    {
        TorrentMetadata torrent = Archive();
        Bitfield everything = new(torrent.PieceCount);

        for (int piece = 0; piece < torrent.PieceCount; piece++)
        {
            everything.Set(piece);
        }

        return new(
            torrent.InfoHash,
            everything,
            Uploaded: 0,
            Downloaded: torrent.TotalLength,
            [.. torrent.Files.Select(one => new ResumeFile(one.Path, one.Length, Modified))]);
    }

    /// <summary>What the files look like now, with at most one of them changed.</summary>
    private static Dictionary<string, ResumeFile> OnDisk(
        TorrentMetadata torrent,
        string? changed,
        long? length = null,
        DateTimeOffset? modified = null)
    {
        Dictionary<string, ResumeFile> files = new(StringComparer.Ordinal);

        foreach (TorrentFileEntry file in torrent.Files)
        {
            files[file.Path] = file.Path == changed
                ? new(file.Path, length ?? file.Length, modified ?? Modified)
                : new(file.Path, file.Length, Modified);
        }

        return files;
    }

    private static TorrentMetadata Archive()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures")))
        {
            directory = directory.Parent;
        }

        return TorrentMetadata.Read(
            File.ReadAllBytes(Path.Combine(directory!.FullName, "tests", "fixtures", "archive-multifile.torrent")));
    }

    /// <summary>A router that does or does not answer.</summary>
    private sealed class FakeMapper(string name, MappedBy by, bool works, string? reason = null) : IPortMapper
    {
        public string Name => name;

        /// <summary>How many times it was asked, so a test can see what was skipped.</summary>
        public int Asked { get; private set; }

        public Task<PortMapResult> MapAsync(int port, CancellationToken ct)
        {
            Asked++;

            return Task.FromResult(works
                ? new PortMapResult(by, port, null)
                : new PortMapResult(MappedBy.Nothing, port, reason ?? "no"));
        }

        public Task UnmapAsync(int port, CancellationToken ct)
        {
            return Task.CompletedTask;
        }
    }
}

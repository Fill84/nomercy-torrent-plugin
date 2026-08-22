using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Bittorrent;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Solver;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tools.Capture;

/// <summary>
/// Saves the page a source really answers into <c>tests/fixtures/</c>.
/// </summary>
/// <remarks>
/// Through the same catalogue, the same fetch and the same solver the plugin
/// uses. A capture taken with a browser by hand, or with curl, is a page the
/// plugin has never seen — and every reader fault in
/// <c>docs/10-known-failures.md</c> § E is a reader that matched something the
/// page did not contain.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] arguments)
    {
        if (arguments.Length == 0)
        {
            Console.Error.WriteLine(
                """
                Capture <source name> [<search term>]
                Capture <source name> --page <address> <file name>
                Capture --file <address> <file name>
                Capture --announce <tracker> <info hash> <file name>
                Capture --udp-announce <host:port> <info hash> <file name>
                Capture --peer <udp tracker host:port> <info hash> <file name>
                Capture --mse <udp tracker host:port> <info hash> <file name>
                Capture --dht <host:port> <info hash>
                Capture --dht-peers <host:port> <info hash>

                  Saves what a source answers into tests/fixtures/. The source name is
                  the one in sources.json, quoted if it has a space.

                  Capture "LimeTorrents" "Silo S03E06"
                  Capture "TorrentFunk" --page https://www.torrentfunk.com/torrent/1/x.html torrentfunk-detail

                  The second form saves one particular address — a row's own page —
                  through that source's gate and its clearance, which is the only way
                  a detail page can be captured at all: the site treats a request with
                  no session as a challenge.
                """);

            return 1;
        }

        if (arguments[0] == "--announce" && arguments.Length > 3)
        {
            return await AnnounceAsync(arguments[1], arguments[2], arguments[3]);
        }

        if (arguments[0] == "--udp-announce" && arguments.Length > 3)
        {
            return await UdpAnnounceAsync(arguments[1], arguments[2], arguments[3]);
        }

        if (arguments[0] == "--peer" && arguments.Length > 3)
        {
            return await PeerAsync(arguments[1], arguments[2], arguments[3]);
        }

        if (arguments[0] == "--dht" && arguments.Length > 2)
        {
            return await DhtAsync(arguments[1], arguments[2]);
        }

        if (arguments[0] == "--dht-peers" && arguments.Length > 2)
        {
            return await DhtPeersAsync(arguments[1], arguments[2]);
        }

        if (arguments[0] == "--mse" && arguments.Length > 3)
        {
            return await PeerAsync(arguments[1], arguments[2], arguments[3], encrypted: true);
        }

        if (arguments[0] == "--peer-at" && arguments.Length > 3)
        {
            string[] where = arguments[1].Split(':');

            return await ShakeHandsAsync(
                where[0],
                int.Parse(where[1]),
                Convert.FromHexString(arguments[2]),
                arguments[3])
                ? 0
                : 1;
        }

        if (arguments[0] == "--file" && arguments.Length > 2)
        {
            // Bytes, not text. A .torrent is bencode and bencode is binary: an
            // info dictionary read as a string and written back has a different
            // SHA-1, and every peer refuses the handshake on it. Nothing else
            // in this tool can save one.
            return await SaveFileAsync(arguments[1], arguments[2]);
        }

        string wanted = arguments[0];
        bool onePage = arguments.Length > 2 && arguments[1] == "--page";
        string term = !onePage && arguments.Length > 1 ? arguments[1] : "Silo S03E06";

        using ILoggerFactory logging = LoggerFactory.Create(builder => builder
            .AddSimpleConsole(console => console.SingleLine = true)
            .SetMinimumLevel(LogLevel.Debug));
        ILogger logger = logging.CreateLogger("capture");

        string repository = RepositoryRoot();
        string fixtures = Path.Combine(repository, "tests", "fixtures");
        Directory.CreateDirectory(fixtures);

        IReadOnlyList<SourceDefinition> shipped = new CatalogueLoader(logger).Load(
            Path.Combine(repository, "src", "NoMercy.Plugin.TorrentDownloader"));

        SourceDefinition? source = shipped.FirstOrDefault(
            candidate => string.Equals(candidate.Name, wanted, StringComparison.OrdinalIgnoreCase));

        if (source is null)
        {
            logger.LogError(
                "No source called '{Name}'. The catalogue has: {Names}.",
                wanted,
                string.Join(", ", shipped.Select(candidate => candidate.Name)));

            return 1;
        }

        // A feed takes no question and is read whole, so its own address is the
        // capture. Refusing would leave the sources that answer "what came out
        // recently" with no fixture at all.
        Uri address = onePage
            ? new(arguments[2])
            : source.SearchAddress is null
                ? new(source.Url)
                : new(Query.Write(source.SearchAddress, term, source.Query));

        // The tool is not the plugin and has no host to ask, so every host is
        // permitted here. The plugin's own grants are the server's business;
        // this only ever runs when somebody typed the command.
        HostGate gate = new(TimeProvider.System);
        gate.Configure(address.Host, TimeSpan.FromSeconds(source.MinimumIntervalSeconds));

        string dataFolder = Path.Combine(repository, "_capture");
        Browser browser = new(
            new BrowserInstall(dataFolder, new PuppeteerBrowserDownloader(), logger),
            new HiddenStages(),
            logger);

        await using PuppeteerTabs tabs = new(browser, logger);
        BrowserSolver solver = new(tabs, logger);

        using HttpClient http = new();
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        ChallengeAwareFetch fetch = new(http, gate, new EverythingPermitted(), new ClearanceStore(), solver, solver);

        logger.LogInformation("Asking {Name} for '{Term}' at {Address}.", source.Name, term, address);

        // The flag belonging to the address being asked. Either flag would do
        // for every source shipped today, but a source with a gated feed and an
        // open search would be walked into the browser for no reason.
        FetchResult result = await fetch.GetAsync(
            address,
            source.SearchAddress is null ? source.Gated : source.SearchAddressGated,
            CancellationToken.None);

        if (!result.Ok)
        {
            logger.LogError("{Name} did not answer: {Reason}", source.Name, result.Failure);

            browser.Dispose();

            return 1;
        }

        // Named for what it is: a JSON body saved as .html is a fixture nobody
        // can open without wondering.
        string extension = result.Body!.TrimStart().StartsWith('{') || result.Body.TrimStart().StartsWith('[')
            ? "json"
            : result.Body.TrimStart().StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
              || result.Body.Contains("<rss", StringComparison.OrdinalIgnoreCase)
                ? "xml"
                : "html";

        string path = Path.Combine(
            fixtures,
            $"{(onePage && arguments.Length > 3 ? arguments[3] : Slug(source.Name))}.{extension}");
        await File.WriteAllTextAsync(path, result.Body!, CancellationToken.None);

        logger.LogInformation(
            "Wrote {Bytes} bytes to {Path}. Read it before writing a reader against it.",
            result.Body!.Length,
            path);

        // A source whose rows name no torrent has one more thing to prove, and
        // it cannot be proved from a saved page: whether the site accepts the
        // signed request the plugin now makes for it. Asked here, in the
        // session that just loaded the page, because that is the only session
        // the token belongs to.
        await ProveTheClaimAsync(source, result.Body!, address, solver, logger);

        browser.Dispose();

        return 0;
    }

    /// <summary>
    /// Asks a site for a torrent it will not print, and says what it answered.
    /// </summary>
    /// <remarks>
    /// The contract was read off the script the page loads, and a contract read
    /// rather than exercised is one nobody has seen the site honour. This is
    /// how it is seen. Nothing is written down: the answer carries a magnet for
    /// a real torrent, and a fixture is not the place for one.
    /// </remarks>
    private static async Task ProveTheClaimAsync(
        SourceDefinition source,
        string body,
        Uri address,
        IInPagePost post,
        ILogger logger)
    {
        if (Readers.Shipped().For(source) is not ISourceReader reader)
        {
            return;
        }

        if (reader.Read(body, address).FirstOrDefault(row => row.Claim is not null) is not SourceRow row)
        {
            return;
        }

        SignedClaim claim = row.Claim!;
        Uri endpoint = SignedMagnet.EndpointOn(row.DetailUrl ?? address);

        logger.LogInformation("Asking {Name} for the torrent of '{Title}' at {Endpoint}.", source.Name, row.Title, endpoint);

        string? answered = await post.PostAsync(
            endpoint,
            SignedMagnet.Body(claim, DateTimeOffset.UtcNow),
            CancellationToken.None);

        if (SignedMagnet.MagnetIn(answered) is string magnet)
        {
            // The hash and nothing else. The rest of a magnet is the release
            // name and a tracker list, and neither is what is being proved.
            logger.LogInformation(
                "{Name} named it: {Hash}.",
                source.Name,
                Magnets.HashOf(magnet) ?? "a magnet carrying no hash");

            return;
        }

        logger.LogWarning(
            "{Name} would not name it. It answered: {Answer}",
            source.Name,
            answered is null ? "nothing at all" : answered[..Math.Min(300, answered.Length)]);
    }

    /// <summary>
    /// Saves one address as the bytes it really is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a <c>.torrent</c>, which is bencode and therefore binary. It goes
    /// straight out over HTTP rather than through the plugin's fetch: the fetch
    /// answers a string, and a string is exactly what must not happen to these
    /// bytes.
    /// </para>
    /// <para>
    /// Straight out, but not unpaced. The plugin's own gate widens the gap
    /// every time a host says it has had enough; this had nothing at all, so a
    /// run of captures walked apibay into a 429 and then kept asking. It waits
    /// and asks again, twice, doubling as it goes — the same shape of answer
    /// the gate gives, in the one place that cannot use it.
    /// </para>
    /// </remarks>
    private static async Task<int> SaveFileAsync(string address, string name)
    {
        string path = Path.Combine(RepositoryRoot(), "tests", "fixtures", name);

        using HttpClient http = new();

        TimeSpan wait = TimeSpan.FromSeconds(15);

        for (int attempt = 1; ; attempt++)
        {
            using HttpResponseMessage answer = await http.GetAsync(address, CancellationToken.None);

            if (answer.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable))
            {
                answer.EnsureSuccessStatusCode();

                byte[] bytes = await answer.Content.ReadAsByteArrayAsync(CancellationToken.None);

                await File.WriteAllBytesAsync(path, bytes, CancellationToken.None);

                Console.Error.WriteLine($"Wrote {bytes.Length} bytes to {path}.");

                return 0;
            }

            if (attempt > 2)
            {
                Console.Error.WriteLine(
                    $"{new Uri(address).Host} answered {(int)answer.StatusCode} three times. "
                    + "It is asking to be left alone; try again later.");

                return 1;
            }

            Console.Error.WriteLine(
                $"{new Uri(address).Host} answered {(int)answer.StatusCode}. "
                + $"Waiting {wait.TotalSeconds:0} seconds and asking once more.");

            await Task.Delay(wait, CancellationToken.None);

            wait *= 2;
        }
    }

    /// <summary>
    /// Announces to a real HTTP tracker and saves what it answered.
    /// </summary>
    /// <remarks>
    /// A real announce for a real public torrent, asking for a handful of peers
    /// and then withdrawing with <c>stopped</c> straight away. The response is
    /// bencode with compact peers in it, and nothing but a tracker produces
    /// one — a hand-written sample would be a parser agreeing with itself.
    /// </remarks>
    private static async Task<int> AnnounceAsync(string tracker, string infoHash, string name)
    {
        byte[] hash = Convert.FromHexString(infoHash);
        string peerId = "-NM0400-" + Guid.NewGuid().ToString("n")[..12];

        string Query(string @event, int want) =>
            $"{tracker}?info_hash={Percent(hash)}"
            + $"&peer_id={Uri.EscapeDataString(peerId)}"
            + $"&port=51413&uploaded=0&downloaded=0&left=6345887744&compact=1&numwant={want}&event={@event}";

        using HttpClient http = new();

        byte[] answer = await http.GetByteArrayAsync(Query("started", 10), CancellationToken.None);

        // Everything the tracker sent, except the addresses in the peer list.
        int peers = Find(answer, "5:peers"u8);

        if (peers >= 0)
        {
            int colon = Array.IndexOf(answer, (byte)':', peers + 7);
            int length = int.Parse(System.Text.Encoding.ASCII.GetString(answer, peers + 7, colon - peers - 7));

            Anonymise(answer.AsSpan(colon + 1, length));
        }

        string path = Path.Combine(RepositoryRoot(), "tests", "fixtures", name);
        await File.WriteAllBytesAsync(path, answer, CancellationToken.None);

        Console.Error.WriteLine($"Wrote {answer.Length} bytes to {path}.");

        // Out of the swarm again immediately: this machine is not seeding an
        // Ubuntu image and should not be offered to anybody as though it were.
        await http.GetAsync(Query("stopped", 0), CancellationToken.None);

        return 0;
    }

    /// <summary>
    /// The same over UDP: connect, then announce, saving both answers.
    /// </summary>
    /// <remarks>
    /// BEP 15 is two exchanges and the first one exists only to get a
    /// connection id, so both are captured — a reader tested against the
    /// announce alone would never have met the sixteen bytes in front of it.
    /// </remarks>
    private static async Task<int> UdpAnnounceAsync(string endpoint, string infoHash, string name)
    {
        string[] parts = endpoint.Split(':');
        using UdpClient udp = new();
        udp.Client.ReceiveTimeout = 8000;
        udp.Connect(parts[0], int.Parse(parts[1]));

        // Connect: the magic protocol id, action 0, and a transaction id we
        // choose and the tracker echoes.
        byte[] connect = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(connect, 0x41727101980L);
        BinaryPrimitives.WriteInt32BigEndian(connect.AsSpan(8), 0);
        BinaryPrimitives.WriteInt32BigEndian(connect.AsSpan(12), 0x1234ABCD);

        await udp.SendAsync(connect, CancellationToken.None);
        UdpReceiveResult first = await udp.ReceiveAsync(CancellationToken.None);

        string folder = Path.Combine(RepositoryRoot(), "tests", "fixtures");
        await File.WriteAllBytesAsync(Path.Combine(folder, $"{name}-connect.bin"), first.Buffer, CancellationToken.None);

        long connectionId = BinaryPrimitives.ReadInt64BigEndian(first.Buffer.AsSpan(8));

        byte[] hash = Convert.FromHexString(infoHash);
        byte[] announce = new byte[98];
        BinaryPrimitives.WriteInt64BigEndian(announce, connectionId);
        BinaryPrimitives.WriteInt32BigEndian(announce.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32BigEndian(announce.AsSpan(12), 0x1234ABCE);
        hash.CopyTo(announce.AsSpan(16));
        System.Text.Encoding.ASCII.GetBytes("-NM0400-" + Guid.NewGuid().ToString("n")[..12]).CopyTo(announce.AsSpan(36));
        BinaryPrimitives.WriteInt64BigEndian(announce.AsSpan(56), 0);
        BinaryPrimitives.WriteInt64BigEndian(announce.AsSpan(64), 6345887744);
        BinaryPrimitives.WriteInt64BigEndian(announce.AsSpan(72), 0);
        BinaryPrimitives.WriteInt32BigEndian(announce.AsSpan(80), 2);
        BinaryPrimitives.WriteInt32BigEndian(announce.AsSpan(84), 0);
        BinaryPrimitives.WriteInt32BigEndian(announce.AsSpan(88), 0);
        BinaryPrimitives.WriteInt32BigEndian(announce.AsSpan(92), 10);
        BinaryPrimitives.WriteUInt16BigEndian(announce.AsSpan(96), 51413);

        await udp.SendAsync(announce, CancellationToken.None);
        UdpReceiveResult second = await udp.ReceiveAsync(CancellationToken.None);

        // The header as sent; the addresses replaced, as over HTTP.
        byte[] announced = second.Buffer;

        if (announced.Length > 20)
        {
            Anonymise(announced.AsSpan(20));
        }

        await File.WriteAllBytesAsync(Path.Combine(folder, $"{name}-announce.bin"), announced, CancellationToken.None);

        Console.Error.WriteLine(
            $"Wrote {first.Buffer.Length} and {second.Buffer.Length} bytes into {folder} as {name}-connect.bin and {name}-announce.bin.");

        // Withdrawn straight away, as over HTTP.
        BinaryPrimitives.WriteInt32BigEndian(announce.AsSpan(80), 3);
        await udp.SendAsync(announce, CancellationToken.None);

        return 0;
    }

    /// <summary>
    /// Asks a real DHT node the four questions and saves what it answers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A DHT node answers a UDP packet from anybody, which is why these can be
    /// captured at all when a peer conversation cannot: nothing has to accept a
    /// connection.
    /// </para>
    /// <para>
    /// Every address in an answer is replaced with TEST-NET-1 before it is
    /// saved, keeping the port and the node id. The nodes a router names are
    /// strangers, and a fixture in a public repository must not publish where
    /// they are.
    /// </para>
    /// </remarks>
    private static async Task<int> DhtAsync(string node, string infoHash)
    {
        string[] parts = node.Split(':');
        byte[] hash = Convert.FromHexString(infoHash);
        byte[] id = RandomNumberGenerator.GetBytes(20);

        using UdpClient udp = new();
        udp.Connect(parts[0], int.Parse(parts[1]));

        (string Name, BencodeValue Query)[] questions =
        [
            ("dht-ping.bin", Krpc("aa", "ping", [new("id"u8.ToArray(), new BencodeBytes(id))])),
            ("dht-find-node.bin", Krpc("ab", "find_node",
            [
                new("id"u8.ToArray(), new BencodeBytes(id)),
                new("target"u8.ToArray(), new BencodeBytes(hash)),
            ])),
            ("dht-get-peers.bin", Krpc("ac", "get_peers",
            [
                new("id"u8.ToArray(), new BencodeBytes(id)),
                new("info_hash"u8.ToArray(), new BencodeBytes(hash)),
            ])),
        ];

        foreach ((string name, BencodeValue query) in questions)
        {
            await udp.SendAsync(Bencode.Write(query), CancellationToken.None);

            using CancellationTokenSource waiting = new(8000);

            try
            {
                UdpReceiveResult answer = await udp.ReceiveAsync(waiting.Token);
                byte[] bytes = Anonymised(answer.Buffer);

                string path = Path.Combine(RepositoryRoot(), "tests", "fixtures", name);
                await File.WriteAllBytesAsync(path, bytes, CancellationToken.None);

                Console.Error.WriteLine($"Wrote {bytes.Length} bytes to {path}.");
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine($"{node} did not answer {name}.");

                return 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// Follows the nodes a router names until one answers with real peers.
    /// </summary>
    /// <remarks>
    /// A router only ever answers <c>get_peers</c> with more nodes; the peers
    /// live further in, on the nodes closest to the hash. This is not the
    /// client's walk — it keeps no routing table and sorts nothing — it exists
    /// only to reach a node that will send a <c>values</c> list, so that the
    /// reader of one is tested against a real answer rather than a made-up one.
    /// </remarks>
    private static async Task<int> DhtPeersAsync(string node, string infoHash)
    {
        string[] parts = node.Split(':');
        byte[] hash = Convert.FromHexString(infoHash);
        byte[] id = RandomNumberGenerator.GetBytes(20);

        using UdpClient udp = new();

        IPEndPoint router = new(
            (await Dns.GetHostAddressesAsync(parts[0]))[0],
            int.Parse(parts[1]));

        List<IPEndPoint> asking = [router];
        HashSet<string> asked = [];

        for (int round = 0; round < 6 && asking.Count > 0; round++)
        {
            List<IPEndPoint> next = [];

            foreach (IPEndPoint one in asking.Take(24))
            {
                if (!asked.Add(one.ToString()))
                {
                    continue;
                }

                byte[] query = Bencode.Write(Krpc("gp", "get_peers",
                [
                    new("id"u8.ToArray(), new BencodeBytes(id)),
                    new("info_hash"u8.ToArray(), new BencodeBytes(hash)),
                ]));

                try
                {
                    await udp.SendAsync(query, one, CancellationToken.None);

                    using CancellationTokenSource waiting = new(2000);

                    UdpReceiveResult answer = await udp.ReceiveAsync(waiting.Token);

                    if (Find(answer.Buffer, "6:valuesl"u8) >= 0)
                    {
                        byte[] bytes = Anonymised(answer.Buffer);
                        string path = Path.Combine(RepositoryRoot(), "tests", "fixtures", "dht-values.bin");

                        await File.WriteAllBytesAsync(path, bytes, CancellationToken.None);

                        Console.Error.WriteLine($"Wrote {bytes.Length} bytes to {path}, after {asked.Count} nodes.");

                        return 0;
                    }

                    next.AddRange(NodesIn(answer.Buffer));
                }
                catch (Exception whatever) when (whatever is OperationCanceledException or SocketException)
                {
                    // A node that does not answer is the normal case out here.
                }
            }

            asking = next;
        }

        Console.Error.WriteLine($"No node named a peer, after {asked.Count} of them.");

        return 1;
    }

    /// <summary>The compact nodes in an answer, as somewhere to ask next.</summary>
    private static IEnumerable<IPEndPoint> NodesIn(byte[] answer)
    {
        int nodes = Find(answer, "5:nodes"u8);

        if (nodes < 0)
        {
            yield break;
        }

        int colon = Array.IndexOf(answer, (byte)':', nodes + 7);
        int length = int.Parse(System.Text.Encoding.ASCII.GetString(answer, nodes + 7, colon - nodes - 7));

        for (int at = colon + 1; at + 26 <= colon + 1 + length && at + 26 <= answer.Length; at += 26)
        {
            yield return new(
                new IPAddress(answer.AsSpan(at + 20, 4)),
                BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(at + 24, 2)));
        }
    }

    /// <summary>One KRPC query, bencoded as BEP 5 puts it.</summary>
    private static BencodeValue Krpc(string transaction, string name, BencodeEntry[] arguments)
    {
        return new BencodeDictionary(
        [
            new("t"u8.ToArray(), new BencodeBytes(System.Text.Encoding.ASCII.GetBytes(transaction))),
            new("y"u8.ToArray(), new BencodeBytes("q"u8.ToArray())),
            new("q"u8.ToArray(), new BencodeBytes(System.Text.Encoding.ASCII.GetBytes(name))),
            new("a"u8.ToArray(), new BencodeDictionary(arguments)),
        ]);
    }

    /// <summary>
    /// Every address in a DHT answer replaced with TEST-NET-1, keeping ports
    /// and node ids.
    /// </summary>
    /// <remarks>
    /// <c>nodes</c> is twenty-six bytes each — twenty of node id, four of
    /// address, two of port — and <c>values</c> is six bytes each. Both are
    /// somebody's address and neither belongs in a public repository.
    /// </remarks>
    private static byte[] Anonymised(byte[] answer)
    {
        byte[] copy = [.. answer];

        // BEP 42's `ip`, which is where the node tells us our own address as it
        // sees it. That is this machine's public address and the one thing in
        // the answer that is nobody else's business at all.
        int mine = Find(copy, "2:ip6:"u8);

        if (mine >= 0)
        {
            Replace(copy.AsSpan(mine + 6, 4), which: 99);
        }

        int nodes = Find(copy, "5:nodes"u8);

        if (nodes >= 0)
        {
            int colon = Array.IndexOf(copy, (byte)':', nodes + 7);
            int length = int.Parse(System.Text.Encoding.ASCII.GetString(copy, nodes + 7, colon - nodes - 7));

            for (int at = colon + 1, which = 1; at + 26 <= colon + 1 + length; at += 26, which++)
            {
                Replace(copy.AsSpan(at + 20, 4), which);
            }
        }

        int values = Find(copy, "6:valuesl"u8);

        if (values >= 0)
        {
            // A list of six-byte strings, each one "6:" and then the peer.
            for (int at = values + 9, which = 1; at + 8 <= copy.Length && copy[at] == (byte)'6'; at += 8, which++)
            {
                Replace(copy.AsSpan(at + 2, 4), which);
            }
        }

        return copy;
    }

    private static void Replace(Span<byte> address, int which)
    {
        address[0] = 192;
        address[1] = 0;
        address[2] = 2;
        address[3] = (byte)which;
    }

    /// <summary>
    /// Shakes hands with a real peer and saves everything it said.
    /// </summary>
    /// <remarks>
    /// A tracker is asked for peers, and each is dialled in turn until one
    /// answers. What is saved is what the <em>peer</em> sent — its handshake and
    /// whatever messages followed, usually a bitfield and a have or two. Only a
    /// real client produces those, and a reader tested against bytes somebody
    /// typed is a reader tested against somebody's idea of the protocol.
    /// </remarks>
    private static async Task<int> PeerAsync(string tracker, string infoHash, string name, bool encrypted = false)
    {
        string[] parts = tracker.Split(':');
        byte[] hash = Convert.FromHexString(infoHash);
        byte[] peerId = System.Text.Encoding.ASCII.GetBytes("-NM0400-" + Guid.NewGuid().ToString("n")[..12]);

        using UdpClient udp = new();
        udp.Client.ReceiveTimeout = 8000;
        udp.Connect(parts[0], int.Parse(parts[1]));

        byte[] connect = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(connect, 0x41727101980L);
        BinaryPrimitives.WriteInt32BigEndian(connect.AsSpan(8), 0);
        BinaryPrimitives.WriteInt32BigEndian(connect.AsSpan(12), 0x2222AAAA);

        await udp.SendAsync(connect, CancellationToken.None);
        UdpReceiveResult connected = await udp.ReceiveAsync(CancellationToken.None);

        byte[] announce = new byte[98];
        BinaryPrimitives.WriteInt64BigEndian(announce, BinaryPrimitives.ReadInt64BigEndian(connected.Buffer.AsSpan(8)));
        BinaryPrimitives.WriteInt32BigEndian(announce.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32BigEndian(announce.AsSpan(12), 0x2222AAAB);
        hash.CopyTo(announce.AsSpan(16));
        peerId.CopyTo(announce.AsSpan(36));
        BinaryPrimitives.WriteInt64BigEndian(announce.AsSpan(64), 6345887744);
        BinaryPrimitives.WriteInt32BigEndian(announce.AsSpan(80), 2);
        BinaryPrimitives.WriteInt32BigEndian(announce.AsSpan(92), 50);
        BinaryPrimitives.WriteUInt16BigEndian(announce.AsSpan(96), 51413);

        await udp.SendAsync(announce, CancellationToken.None);
        UdpReceiveResult answered = await udp.ReceiveAsync(CancellationToken.None);

        // Our own handshake: the length, the name, the reserved bytes with the
        // extension and DHT bits on, the info hash and who we are.
        byte[] handshake = new byte[68];
        handshake[0] = 19;
        "BitTorrent protocol"u8.CopyTo(handshake.AsSpan(1));
        handshake[25] = 0x10;
        handshake[27] = 0x01;
        hash.CopyTo(handshake.AsSpan(28));
        peerId.CopyTo(handshake.AsSpan(48));

        _ = handshake;

        for (int at = 20; at + 6 <= answered.Buffer.Length; at += 6)
        {
            IPAddress address = new(answered.Buffer.AsSpan(at, 4));
            int port = BinaryPrimitives.ReadUInt16BigEndian(answered.Buffer.AsSpan(at + 4, 2));

            if (await ShakeHandsAsync(address.ToString(), port, hash, name, encrypted))
            {
                return 0;
            }
        }

        Console.Error.WriteLine("No peer answered.");

        return 1;
    }

    /// <summary>
    /// Dials one peer, shakes hands, and saves everything it says back.
    /// </summary>
    /// <remarks>
    /// The reserved bytes carry the extension bit on byte five and the DHT bit
    /// on byte seven, exactly as the client will send them — a peer that reads
    /// them decides what it offers us on the strength of them, so a capture
    /// taken without them is a conversation this client will never have.
    /// </remarks>
    private static async Task<bool> ShakeHandsAsync(string host, int port, byte[] hash, string name, bool encrypted = false)
    {
        byte[] peerId = System.Text.Encoding.ASCII.GetBytes("-NM0400-" + Guid.NewGuid().ToString("n")[..12]);

        byte[] handshake = new byte[68];
        handshake[0] = 19;
        "BitTorrent protocol"u8.CopyTo(handshake.AsSpan(1));
        handshake[25] = 0x10;
        handshake[27] = 0x01;
        hash.CopyTo(handshake.AsSpan(28));
        peerId.CopyTo(handshake.AsSpan(48));

        try
        {
            using TcpClient peer = new();
            await peer.ConnectAsync(host, port, new CancellationTokenSource(4000).Token);

            Stream stream = peer.GetStream();

            if (encrypted)
            {
                // MSE, which is what a great many peers insist on before they
                // will say anything at all. The handshake travels with it, so
                // what is read back is already in the clear.
                MseLink link = await MseNegotiation.InitiateAsync(
                    stream,
                    hash,
                    handshake,
                    MseMethod.Plaintext | MseMethod.Rc4,
                    RandomNumberGenerator.Create(),
                    new CancellationTokenSource(8000).Token);

                Console.Error.WriteLine($"{host}:{port} agreed {link.Method}.");

                stream = link.Stream;
            }
            else
            {
                await stream.WriteAsync(handshake, CancellationToken.None);
            }

            // Interested, as a real client says straight after handshaking. A
            // peer that hears nothing from us has no reason to say anything
            // back, and the capture is then a handshake and silence.
            await stream.WriteAsync(new byte[] { 0, 0, 0, 1, 2 }, CancellationToken.None);

            using MemoryStream heard = new();
            byte[] buffer = new byte[16 * 1024];
            CancellationTokenSource listening = new(15000);

            while (heard.Length < 128 * 1024)
            {
                int read = await stream.ReadAsync(buffer, listening.Token);

                if (read == 0)
                {
                    break;
                }

                heard.Write(buffer.AsSpan(0, read));
            }

            // A handshake and nothing else is a peer that hung up on a
            // stranger: worth keeping once, and not what a reader of messages
            // can be tested against.
            if (heard.Length <= 68)
            {
                Console.Error.WriteLine($"{host}:{port} shook hands and said nothing else.");

                return false;
            }

            string path = Path.Combine(RepositoryRoot(), "tests", "fixtures", name);
            await File.WriteAllBytesAsync(path, heard.ToArray(), CancellationToken.None);

            Console.Error.WriteLine($"Wrote {heard.Length} bytes to {path}, from {host}:{port}.");

            return true;
        }
        catch (Exception whatever) when (whatever is SocketException or OperationCanceledException or IOException)
        {
            Console.Error.WriteLine($"{host}:{port} did not answer.");

            return false;
        }
    }

    /// <summary>
    /// Replaces the address of every compact peer, keeping its port.
    /// </summary>
    /// <remarks>
    /// A tracker answers with the addresses of real people, and the first one
    /// in the list is usually this machine. A fixture in a public repository
    /// must not publish either, so the four address bytes of each six become
    /// TEST-NET-1 — the range reserved for documentation — and everything else
    /// the tracker sent, the lengths and the intervals and the order, stays
    /// exactly as it arrived. What the parser is tested on is the shape, and
    /// the shape is untouched.
    /// </remarks>
    private static void Anonymise(Span<byte> peers)
    {
        for (int at = 0; at + 6 <= peers.Length; at += 6)
        {
            peers[at] = 192;
            peers[at + 1] = 0;
            peers[at + 2] = 2;
            peers[at + 3] = (byte)(at / 6 + 1);
        }
    }

    private static int Find(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        return haystack.AsSpan().IndexOf(needle);
    }

    /// <summary>
    /// Twenty raw bytes, percent-encoded one byte at a time.
    /// </summary>
    /// <remarks>
    /// Byte by byte, and never through a string. An info hash is bytes, not
    /// text: putting it through a text encoder turns every byte above 0x7F into
    /// two, and the tracker answers "not authorized" for a torrent it has —
    /// which is what happened the first time this was written.
    /// </remarks>
    private static string Percent(byte[] bytes)
    {
        System.Text.StringBuilder text = new(bytes.Length * 3);

        foreach (byte value in bytes)
        {
            if (char.IsAsciiLetterOrDigit((char)value) || value is (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~')
            {
                text.Append((char)value);
            }
            else
            {
                text.Append('%').Append(value.ToString("X2"));
            }
        }

        return text.ToString();
    }

    /// <summary>A file name that is the source's name and nothing clever.</summary>
    private static string Slug(string name)
    {
        return new string([.. name.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')]);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("This tool must be run from inside the repository.");
    }
}

/// <summary>
/// Every host permitted, because this tool has no host to ask.
/// </summary>
/// <remarks>
/// Only ever reached when somebody typed the command, which is the consent the
/// plugin's grants exist to obtain.
/// </remarks>
internal sealed class EverythingPermitted : IPluginGrants
{
    public Task<bool> HasAsync(string kind, string scope, CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<string>> GetAsync(string kind, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    public Task RequestAsync(string kind, string scope, string reason, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}

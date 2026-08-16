using System.Buffers.Binary;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
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

        browser.Dispose();

        return 0;
    }

    /// <summary>
    /// Saves one address as the bytes it really is.
    /// </summary>
    /// <remarks>
    /// For a <c>.torrent</c>, which is bencode and therefore binary. It goes
    /// straight out over HTTP rather than through the plugin's fetch: the fetch
    /// answers a string, and a string is exactly what must not happen to these
    /// bytes.
    /// </remarks>
    private static async Task<int> SaveFileAsync(string address, string name)
    {
        string path = Path.Combine(RepositoryRoot(), "tests", "fixtures", name);

        using HttpClient http = new();
        byte[] bytes = await http.GetByteArrayAsync(address, CancellationToken.None);

        await File.WriteAllBytesAsync(path, bytes, CancellationToken.None);

        Console.Error.WriteLine($"Wrote {bytes.Length} bytes to {path}.");

        return 0;
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

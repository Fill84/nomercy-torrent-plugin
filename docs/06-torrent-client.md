# The torrent client

The plugin has its own. The BitTorrent protocol is written in this repository — no third-party
torrent library. Only the .NET base library: sockets, files, SHA-1, cryptography.

It lives in `NoMercy.Plugin.TorrentDownloader.Bittorrent`, which references nothing else in the
solution and is tested against captured wire bytes.

0.3.4 already had bencode, torrent metadata, magnets, HTTP and UDP trackers, the peer handshake and
messages, metadata exchange, the bitfield, a piece store, a verifier, resume data, a swarm
coordinator and a piece server. It had **no** DHT, peer exchange, local discovery, rate limits,
seeding policy, port mapping, choking, rarest-first or endgame. 0.4.0 has all of it.

## Specs implemented

| Part | Spec |
| --- | --- |
| Bencode | BEP 3 |
| `.torrent`, info hash | BEP 3 |
| Magnet links | BEP 9 |
| Metadata from peers | BEP 9 (`ut_metadata`) |
| Peer wire protocol | BEP 3 |
| Extension protocol | BEP 10 |
| HTTP tracker announce and scrape | BEP 3, compact peers BEP 23 |
| UDP tracker announce and scrape | BEP 15 |
| DHT | BEP 5 |
| Peer exchange | BEP 11 (`ut_pex`) |
| Local peer discovery | BEP 14 |
| Private torrents | BEP 27 |
| Message stream encryption | MSE/PE |
| Port mapping | UPnP IGD, NAT-PMP |
| Fast resume | own format |

## The port

`Core` never sees the implementation:

```csharp
public interface ITorrentEngine
{
    Task<TorrentHandle> AddAsync(TorrentRequest request, CancellationToken ct);
    Task<IReadOnlyList<TorrentStatus>> StatusAsync(CancellationToken ct);
    Task PauseAsync(string infoHash, CancellationToken ct);
    Task ResumeAsync(string infoHash, CancellationToken ct);
    Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken ct);
    Task<IReadOnlyList<TorrentFile>> FilesAsync(string infoHash, CancellationToken ct);
}

public sealed record TorrentRequest(
    string Source,                    // magnet URI or a .torrent URL
    IReadOnlyList<string> Trackers,   // merged from every indexer that had it
    string DownloadFolder,
    long? ExpectedBytes);

public sealed record TorrentStatus(
    string InfoHash, string Name, TorrentState State,
    long BytesDone, long BytesTotal,
    double DownloadRateBytesPerSecond, double UploadRateBytesPerSecond,
    int Peers, int Seeds, double Ratio, TimeSpan? Eta, string? Error);

public enum TorrentState
{
    FetchingMetadata, Checking, Downloading, Seeding, Stalled, Paused, Stopped, Error
}
```

`FetchingMetadata` and `Stalled` are their own states. A magnet has no file list until metadata
arrives, and reporting that as "0% downloading" makes a torrent that will never resolve look like
one about to start.

## Bencode

Reader and writer over `ReadOnlySpan<byte>`. Bencode is byte-oriented: a name can be non-UTF-8, and
the info hash is SHA-1 over the **raw bytes of the `info` dictionary as they arrived**. Decoding to
a string and re-encoding gives a different hash and every peer refuses the handshake. The reader
records the byte range of `info` while parsing.

## Metadata

`.torrent`: `info.name`, `info.piece length`, `info.pieces` (20 bytes each), `info.length` or
`info.files`, `announce`, `announce-list`, `info.private`.

Magnet: `xt=urn:btih:` in hex (40 chars) or base32 (32 chars), `dn`, every `tr`.

A magnet has no `info`. It is fetched over BEP 9: handshake, extension handshake advertising
`ut_metadata`, request the metadata pieces, verify SHA-1 against the info hash. A peer whose
metadata does not hash correctly is dropped.

Metadata that has not arrived within `MetadataTimeoutMinutes` fails the torrent, blacklists the hash
with the reason, and returns the episode to missing.

## Trackers

**HTTP** — GET with `info_hash`, `peer_id`, `port`, `uploaded`, `downloaded`, `left`, `compact=1`,
`event`. Bencode response with `interval` and `peers`; peers are usually compact — six bytes each,
four for the address and two big-endian for the port.

**UDP** — connect (magic `0x41727101980`, transaction id), then announce. A connection id lives one
minute. Retry backoff is `15 * 2^n` seconds, up to eight tries.

Announce at `interval`, and on `started`, `completed`, `stopped`. Every tracker in the merged list is
announced to in parallel; one failing does not stop the others.

## Peer wire

Handshake: `19` `BitTorrent protocol` `8 reserved` `info_hash` `peer_id`. Reserved bit `0x100000` on
byte 5 advertises the extension protocol; bit `0x01` on byte 7 advertises DHT.

Messages: `choke`, `unchoke`, `interested`, `not interested`, `have`, `bitfield`, `request`, `piece`,
`cancel`, `port`, `extended`. Blocks are 16 KiB, with up to `PipelineDepth` requests in flight per
peer, adjusted to that peer's rate.

A peer sending a block nobody requested is dropped. A piece failing its SHA-1 is discarded and the
peers that contributed are penalised; two failed pieces bans a peer for the session.

## Encryption

MSE/PE: Diffie-Hellman key exchange, RC4 stream, both `1` (plaintext) and `2` (RC4) crypto methods
offered. Allowed, never required — a peer that refuses it is still used in plaintext. Outgoing
connections try encrypted first and fall back.

## Piece picking

- **Rarest first**, from bitfields and `have` messages.
- **Random among the first four pieces** at the start, so something can be verified early.
- **Endgame**: below `EndgamePieces` remaining, request outstanding blocks from every unchoked peer
  and cancel the losers.
- Blocks within a piece are requested in order so the piece completes and can be verified.

## Choking

Every ten seconds, unchoke the four interested peers with the best download rate. Every thirty
seconds, optimistically unchoke one at random. While seeding, rank by upload rate.

## DHT

Kademlia over UDP on the same port. Routing table of 160 buckets, split on insert, `k = 8`.
`ping`, `find_node`, `get_peers`, `announce_peer`. Bootstrapped from the shipped node list on first
run, then persisted to the data folder and reloaded, so a restart does not start from nothing.

**The shipped node list**, which nothing said before `S5-09` and which was measured rather than
copied. On 18 August 2026 these answered a `ping` from a machine here:

- `dht.transmissionbt.com:6881`
- `dht.libtorrent.org:25401`

`router.bittorrent.com:6881` and `router.utorrent.com:6881` — the two everybody quotes — answered
nothing at all. The captured packets in `tests/fixtures/dht-*.bin` came from the first of the two
that did.

What is persisted is the node list **and this client's own node id**. An id that changes on every
restart is a client every other table has to learn again, and every `announce_peer` it ever made is
thrown away with it.

A torrent whose metadata says `private` never touches the DHT.

## Peer exchange and local discovery

`ut_pex` over the extension protocol: added and dropped peers read and offered, at most once a
minute per peer. Local peer discovery announces on the multicast group and reads other announces.

Both disabled for a private torrent.

## Disk

- Files created sparse at full size.
- Writes through a bounded buffer, flushed per completed piece.
- SHA-1 verification before a piece counts as done.
- Reads for uploading from a small LRU cache.
- A multi-file torrent is one byte stream across files; a piece can straddle a boundary.

## Fast resume

Written on a clean stop and every `ResumeInterval`: info hash, the bitfield of verified pieces,
bytes up and down, and each file's size and modification time.

On load, a file whose size or timestamp does not match is re-verified. A crash costs one interval of
verification, not the whole torrent.

## Rate limits

Token buckets: one global pair, one pair per torrent, refilled on a timer and drained by every read
and write. `MaxDownloadRate` and `MaxUploadRate` from settings, live-adjustable, zero meaning
unlimited. The limit is on the line and not on a torrent, so one bucket for each direction is shared
between every torrent. It holds one second's worth, so a burst of blocks goes through unharmed while
the average comes out at the number the owner typed.

## What is downloaded

**Video files, and nothing else.** The list of what counts is a whitelist in
`Staging.VideoExtensions`; a type that is not on it is not downloaded, whatever it is. Samples are
not downloaded either — a video under 50 MB beside something bigger, or any file with `sample` in
its path.

The choice is made when the metadata arrives, before a byte is asked for. The wanted files become a
mask of pieces and the picker offers nothing outside it. A piece straddling a wanted file and an
unwanted one is fetched, because a piece is the smallest thing a swarm hands over; the fragment of
the neighbour that comes with it is never staged.

A torrent with no video file in it is **refused**: it is paused, the reason is recorded against the
grab, and nothing of it is downloaded. That is the shape a fake release takes — on 22 August 2026
one was a 1.2 GB executable named after an episode, and it downloaded to completion because the only
thing that knew what a video file was ran afterwards rather than before.

Progress is measured against what is being downloaded, not against what the torrent weighs.

## Requests

Four pieces are asked of a peer at a time. One at a time leaves it idle for a round trip between
finishing one piece and being asked for the next.

A piece is marked as on its way when it is requested, and the picker offers nobody a piece already
on its way. **A piece that has not been answered for a minute is given back**, along with whatever
was assembled of it. Without that the mark is only ever cleared by the piece arriving or failing its
hash, so a peer that took a request and went quiet kept that piece for the rest of the run — and
with peers joining and leaving the marked pieces pile up until the picker has nothing to offer
anybody. That is a download sitting at nought bytes a second with seeds on it, which is what
happened on 22 August 2026.

Every request this client makes is made in answer to a message, so a peer that goes quiet is never
asked again on its own. Each connection therefore has a beat — a quarter of the patience above — on
which it asks again.

## Uploading

**A public torrent never uploads.** Not while it is downloading, not once it is finished. A peer on
a public torrent is never unchoked and a request from one is never answered, so its ratio stays at
nought.

This is the owner's rule, decided on 22 August 2026 after the Downloads page showed a public torrent
at 0.2% downloaded with a ratio of 0.17. It costs download speed: a swarm reciprocates, and a client
that gives nothing back is choked by the peers that notice. That is the trade the owner chose.

Only a torrent whose metadata says `private` uploads, because there the tracker keeps an account of
what the owner has given back and a client that took without giving would cost them it.

## Seeding

A private torrent seeds until `SeedRatio` **or** `SeedHours`, whichever comes first. A public one is
finished the moment it is complete: nothing is ever uploaded on a public swarm, so staying in one
gives nothing to anybody while costing a connection. It is stopped, not removed — the files stay
where they are and staging takes them from there.

## Stalls

No progress **and** no peers for `StallMinutes`: the torrent is stopped, the reason is recorded
against the grab and the episode returns to missing. Progress with no peers is not a stall; peers with no
progress for a minute is not either.

## Ports

`ListenPort` from settings, TCP and UDP. Mapped with **UPnP IGD**, falling back to **NAT-PMP**. A
mapping that cannot be made is reported on the Settings page with the reason and the client carries
on — a server behind a router that refuses both still downloads from peers it dials out to.

## Private torrents

When `info.private` is 1: no DHT, no peer exchange, no local peer discovery, announce only to the
torrent's own trackers, and it is the only kind of torrent this client uploads on — see
**Uploading**. The passkey is read through `IPluginSecretStore` and never appears in a log
line, an error message, a page, or the activity journal.

## Around the transfer

| Concern | Rule |
| --- | --- |
| Where files land | the incomplete folder while downloading, then staged to the intake folder |
| Which file is the episode | the largest video; samples, subtitles and NFOs ignored |
| How many at once | `MaxConcurrentDownloads`, default 5. The rest wait and report as **queued**, oldest first. A finished torrent does not hold a slot: it is seeding, not downloading. |
| Trackers | `DefaultTrackers` plus every tracker the find stage merged |

## Lifecycle

```
Initialize  → sockets bound, port mapped, DHT bootstrapped, resume loaded
Grab        → AddAsync(...) → info hash
Transfers   → StatusAsync() → one row per torrent
Complete    → the largest video staged, the encode dispatched, seeding continues
Dispose     → announce stopped, resume written, sockets closed, mapping removed
```

Started once when the plugin initialises, stopped once on dispose. Not per cadence.

## Recovery

| Situation | What happens |
| --- | --- |
| in the store, in the engine | carry on |
| in the store, not in the engine | re-added from its magnet, resume intact |
| in the engine, not in the store | stopped, files kept, logged |
| finished while the server was down | staged and dispatched on the first transfers tick |

## What is visible

Metadata fetch started and from how many peers, metadata timed out, verification progress, download
progress with rate, peers and seeds, ratio while seeding, stall detected, paused, resumed,
completed, staged, port mapping succeeded or failed, error in its own words — all to the activity
journal.

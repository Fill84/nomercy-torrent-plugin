# Torrent engine — design

**Decided 2026-08-08.** Replaces stage 3 of the
[plugin design](2026-07-29-torrent-download-plugin-design.md), which assumed the plugin would drive
an external client (qBittorrent, Transmission, Deluge) over its API.

The plugin downloads by itself. No external torrent application, no daemon to install, no
credentials to configure. A user installs the plugin and it works.

---

## 1. Why this changed

The original stage 3 was `ITorrentClient`, an adapter over somebody else's client. That contradicts
what this product promises: a user who wants missing episodes downloaded should not first have to
install, configure and secure a second application, then teach the plugin how to talk to it.

Three routes were weighed. All three satisfy "no external application":

| Route | Verdict |
| --- | --- |
| MonoTorrent as an embedded library (MIT, actively maintained, last commits 2026-07-31) | Rejected by the owner |
| Own engine, built up from the research in `deepseepk-torrent-client.md` | **Chosen** |
| MonoTorrent now behind `ITorrentEngine`, own engine later | Rejected by the owner |

The recommendation was MonoTorrent, on the grounds that it already handles magnet, DHT, UDP
trackers, multi-file and the twenty years of edge cases that decide whether a download stalls at
99%. The owner chose the own engine for full control and zero dependencies, with the trade-off
stated and understood. Recorded here so the reasoning is not relitigated later.

## 2. Requirements

From the owner, verbatim in intent:

1. **No external application.** Everything inside the plugin process.
2. **Multi-file, magnet, DHT and UDP trackers** — the research skeleton has none of these and they
   are not optional. Season packs are always multi-file; most public content is magnet-only.
3. **Never seed.** Uploading happens only where BitTorrent's tit-for-tat requires it to keep
   download speed up, and no further.
4. **Maximum speed, no excuses.** Connect to as many peers and seeds as possible, always.
5. **Forced encryption.** MSE/PE on every connection. Unencrypted peers are refused, not
   downgraded to.
6. **Complete means stopped.** On completion every connection closes and the torrent is done. The
   chain continues from there.
7. **Resume across restarts.** A reboot mid-download does not start a 40 GB season pack over.

### Recorded consequences

These follow from the requirements and are accepted, not open questions:

- **Private trackers will ban the account.** Requirement 3 produces a ratio of zero. On the Torznab
  path — which is the private tracker protocol — that ends in a ban. Public trackers do not care.
- **Forced encryption costs a few peers.** Requirement 5 excludes peers that cannot speak MSE. In
  practice this is old or misconfigured clients only; it is the same setting as qBittorrent's
  "Require encryption". The loss is small and the throttling protection is worth it.

## 3. Non-goals

- **Seeding.** There is deliberately no component that serves pieces to other peers. The minimal
  reciprocation requirement 3 allows lives in `SwarmPolicy` as a bounded exception, so it cannot
  quietly grow into a general capability.
- **Being a general-purpose torrent client.** No web UI of its own, no torrent creation, no RSS (the
  plugin already has indexers), no bandwidth scheduling.
- **Replacing the intake pipeline.** The engine's job ends when the bytes are on disk and verified.

## 4. Components

Nine units. The separation that carries the whole design: **only the coordinator owns state.**
Everything else is either pure or owns nothing but itself.

| Unit | Responsibility | Owns state |
| --- | --- | --- |
| `Bencode` | Parse and encode. Bytes in, bytes out | no — pure |
| `Metadata` | `.torrent` and magnet URI to torrent identity, piece lengths, file list with offsets. Computes the info hash | no — pure |
| `IPeerSource` | Yields peer endpoints. Three implementations: `HttpTracker`, `UdpTracker`, `Dht` | own session |
| `PeerConnection` | One connection. MSE handshake, then BT handshake, then framed messages | itself only |
| `TorrentCoordinator` | **The single owner.** Bitfield, availability, in-flight blocks, the peer set | yes |
| `PieceVerifier` | Hashes a completed piece against its expected SHA-1 | no — pure |
| `PieceStore` | Writes verified pieces into the multi-file layout, async | disk |
| `ResumeStore` | Persists the bitfield, reloads it at startup | disk |
| `SwarmPolicy` | How many peers, how much to give back, when to stop | no — pure |

Above them one facade, `ITorrentEngine`: add by magnet or `.torrent`, remove, read progress, and an
event on completion. That is the entire surface the orchestrator needs, which is why stage C will
know nothing about peer protocols.

Two boundaries are deliberate and worth stating:

**`SwarmPolicy` is separate from the coordinator.** Requirements 3, 4 and 6 are policy, not
mechanism. Held apart they are testable without opening a socket, and tunable without touching the
coordinator.

**`PieceVerifier` is separate from `PieceStore`.** Verifying is pure computation and trivially
testable; writing touches disk. Together they would be one class testable only against a real
directory.

### Policy values

"As many peers as possible" and "a generous timeout" are not implementable as written, so
`SwarmPolicy` carries them as named settings with defaults. All are user-configurable; the defaults
are the starting point, not a ceiling discovered by measurement.

| Setting | Default | Reasoning |
| --- | --- | --- |
| `MaxConnectionsPerTorrent` | 100 | The research addendum suggested 50-75. Requirement 4 argues for more, and the coordinator model does not degrade the way a lock does. Tune upward once slice 5 can measure it |
| `MaxHalfOpenConnections` | 20 | Connection attempts in flight. Too high and a home router's NAT table suffers, which slows everything including playback |
| `NoPeersTimeout` | 30 minutes | Before declaring a torrent dead so the orchestrator tries the next release. Long enough that a slow-to-populate swarm is not abandoned, short enough that a dead release does not park the queue |
| `MetadataTimeout` | 5 minutes | Magnet metadata via BEP 9. A swarm with peers should answer in seconds; this is the give-up point |
| `MaxPieceFailuresPerPeer` | 3 | Ban threshold. One failure is luck, three is a pattern |
| `EndgameThreshold` | 5% remaining | When to start requesting outstanding blocks from several peers at once |

## 5. Concurrency model

One owner per torrent, peers send messages to it.

The research skeleton guards a shared piece manager with a lock and caps connections with a
`SemaphoreSlim`. That holds at ten peers. At the hundred peers requirement 4 asks for, the shared
state becomes the bottleneck: every arriving block makes every other peer wait, exactly when
throughput matters most. It is also the hardest model to make race-free — the failures are
non-reproducible and only appear under load.

Instead: per torrent there is exactly one coordinator that owns all mutable state. Peer connections
own nothing. They post "I have piece X" or "give me work" and receive an answer. No locks, no
races, because there is one writer.

This is where libtorrent and MonoTorrent both end up, and it pays a second dividend: with one owner
of the bitfield there is exactly one place that knows what is complete, and therefore exactly one
place that persists it. Resume falls out of the model rather than being bolted on.

## 6. Data flow

```
magnet / .torrent
      │
      ▼
 Metadata ──── magnet? ──▶ fetch metadata from peers (BEP 10 + BEP 9)
      │
      ▼
 ResumeStore ──▶ known info hash? reload the bitfield
      │
      ▼
 IPeerSource ×3 ──▶ HTTP tracker, UDP tracker, DHT — continuous, not one-shot
      │
      ▼
 TorrentCoordinator ──▶ opens connections up to SwarmPolicy's ceiling
      │                  replaces dead peers from the pool
      ▼
 PeerConnection ──▶ MSE handshake ──▶ BT handshake ──▶ bitfield
      │
      ▼  "I have X" / "give me work"
 TorrentCoordinator ──▶ assigns blocks (rarest first)
      │
      ▼  block in, piece complete
 PieceVerifier ──▶ hash good? ──▶ PieceStore writes ──▶ ResumeStore records
      │                   └─ bad ──▶ re-request, debit the contributing peers
      ▼
 all pieces ──▶ close everything ──▶ completion event ──▶ intake
```

Three parts of this are absent from the research skeleton and are not optional.

**Magnet requires metadata exchange.** A magnet link carries only the info hash, some trackers and a
display name. Piece lengths and the file list come from the peers themselves, over the extension
protocol (BEP 10) and the metadata extension (BEP 9). Without both, magnet support does not exist.

**Discovery is continuous.** Announcing once yields the peers of that moment. Requirement 4 needs
repeated announces and a DHT that keeps searching, so the ceiling stays filled as peers drop out.

**Endgame mode.** At the tail a few blocks sit with slow peers and the download parks at 99% waiting
on one bad connection. The fix is to request the last outstanding blocks from several peers at once
and take whichever arrives first. This is the gap the research names as "no re-request for missed
blocks", and it is the difference between 99% and done.

A consequence of multi-file: pieces cross file boundaries. A 2 MB piece can hold the end of episode
3 and the start of episode 4, so `PieceStore` maps piece offsets to file-plus-offset, possibly
spanning several files.

## 7. Error handling

In BitTorrent, failure is the steady state. Peers vanish, lie, and refuse to answer. If each of
those reaches the user, the plugin is the opposite of effortless. So failures split in two.

### Noise — handled internally, never surfaced

| Condition | Response |
| --- | --- |
| Peer refuses, times out, or drops | Discard, take the next from the pool. Happens hundreds of times per download |
| Peer sends malformed or out-of-protocol messages | Disconnect, remember as bad |
| One tracker down | Exponential backoff for that tracker; other sources continue |
| DHT unreachable | Fall back to trackers |
| Piece hash mismatch | Discard the piece, request it again |

That last one has a subtlety. A piece is assembled from blocks contributed by several peers, so a
bad hash does not identify the culprit. The coordinator therefore tracks which peers contributed to
which pieces; a peer that recurs across failed pieces is banned. One failure is luck, three is a
pattern.

### Facts — something must act

| Condition | Response |
| --- | --- |
| No peers at all, past a generous timeout | Report the download failed so the orchestrator tries the next release |
| Metadata never arrives (magnet) | Same — the torrent is dead |
| Disk full or write error | Fatal for this torrent, reported with the real reason. The only case the user must act on |

### The invariant

> The resume record never claims more than the disk actually holds.

The order is therefore: write the piece, flush, then record it. A crash between those two costs one
piece re-downloaded, a few megabytes. The reverse would leave a silently corrupt file that the user
discovers while watching. This mirrors the grab invariant the plugin already keeps: an incomplete
handoff is never recorded as a finished one.

Resume follows from it. On startup the record is not trusted blindly — what it claims is verified
against the disk, lazily, while downloading is already running, so a file the user deleted or a
sector that rotted is re-fetched instead of producing a broken result.

## 8. Testing

`Core` is tested with no host present. That convention produced 257 tests across stages 0a and 0b,
and a network stack does not have to be the exception, given two design choices made up front.

**`PeerConnection` takes a `Stream`, not a socket.** Both ends of a connection can then be driven in
one process: both handshakes, message framing, a half-arrived message, garbage on the wire — all
testable without opening a port.

**The coordinator is the single owner**, which makes it a pure state machine: messages in,
decisions out. It is tested by posting messages and asserting choices — "given these four peers with
these bitfields, which block does it request, and from whom?", "does it ban after three failed
pieces and not after two?". No network, no timing, no flakiness.

| Layer | What it proves |
| --- | --- |
| Pure units | Bencode round-trip, malformed input, info hash against known values, offset mapping across file boundaries, policy decisions |
| Connection against a fake stream | Both handshakes, framing, truncated and malformed messages |
| Coordinator via messages | Block selection, endgame, hash failure, peer bans, completion |
| **In-process test seeder** | The whole chain, magnet to complete file |

The test seeder is the useful part. The product never seeds, but the **test harness** does. A
minimal seeder offering a known torrent over an in-process connection gives real, complete downloads
in a test — multi-file included, resume-after-simulated-restart included, encryption negotiation
included. No network, no external client, fully deterministic.

Because we write that seeder, it can also **lie**: send a deliberately corrupt piece, hang up
mid-piece, stall without answering, refuse encryption. That proves section 7 instead of hoping for
it. Error handling that has never been provoked usually does not work.

Beyond the hermetic suite there are two opt-in verification paths, neither of which runs in CI:

- One integration test against a real public torrent (a Linux ISO), enabled by an explicit flag, to
  prove the engine works against a real swarm.
- Deployment to `beast-unit` over SSH for end-to-end verification on a real server, with the browser
  available to inspect the UI once stage D exists. **Restart the media-server service, never the
  machine.** `beast-unit` also hosts the six self-hosted CI runners; rebooting the box takes those
  down with it, and it is somebody's media server while they are watching.

CI stays hermetic and fast. A red build has to mean a real regression, not a flaky swarm.

Every test must fail if the behaviour breaks. No tests that only assert something exists.

## 9. Build order

Vertical slices, the way 0a and 0b were built. Each slice is something demonstrably working, not a
layer that only does something at the end.

| # | Slice | Proves |
| --- | --- | --- |
| 1 | Bencode, `.torrent` metadata, single file, HTTP tracker, plain handshake, verify and write | A complete download against the test seeder |
| 2 | Forced MSE encryption | Handshake negotiated; unencrypted peers refused |
| 3 | Multi-file layout | Pieces spanning file boundaries land correctly |
| 4 | Resume — persist the bitfield, verify lazily on start | A killed process resumes without re-downloading |
| 5 | Endgame and `SwarmPolicy` | No stall at 99%; peer ceiling respected; stops on completion |
| 6 | UDP trackers | Announce over UDP |
| 7 | Magnet — BEP 10 extension protocol, BEP 9 metadata | A magnet link downloads with no `.torrent` |
| 8 | DHT | Peers found with no working tracker |
| 9 | `ITorrentEngine` facade and completion event | Stage C can consume it without knowing any protocol |

## 10. Where this sits

This is subsystem **A** of four. The plugin as a whole decomposes as:

| | Part | Depends on |
| --- | --- | --- |
| **A** | **Engine** — this document | nothing |
| B | Store — SQLite/Dapper: monitored shows, wanted episodes, grabs, transfers, blacklist | nothing |
| C | Orchestrator — the six-stage loop and the handoff into intake | A and B |
| D | Surface — REST plus the NM design-system views | B and C |

A and B are independent. Each gets its own spec, plan and implementation cycle.

One simplification this design hands to C: the original plan moved completed files into intake by
hardlink, "so seeding continues from the same bytes". With requirement 3 that reason is gone, so a
plain move will do.

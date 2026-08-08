# Torrent Engine, Part 1 — A Working Downloader Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A resumable, forced-encrypted, multi-file BitTorrent downloader in `Core` that completes a real torrent against an in-process test seeder, with no host and no network.

**Architecture:** One owner per torrent. `TorrentCoordinator` holds every piece of mutable state — bitfield, availability, in-flight blocks, the peer set — and peers own nothing. Peer connections post messages to it and receive work back, so there are no locks and no races. `PeerConnection` speaks to a `Stream`, never a socket, which is what makes the whole stack testable in one process.

**Tech Stack:** C# / .NET 10, xUnit, FluentAssertions. `Core` has zero package references and must keep it that way.

## Global Constraints

- **Target framework:** `net10.0`. `Nullable` enabled, `ImplicitUsings` enabled, `LangVersion` latest.
- **`TreatWarningsAsErrors` is true.** A warning fails the build.
- **`Core` takes no package references.** Everything here is BCL only. No `MonoTorrent`, no JSON library, no DI container.
- **`Core` has no host dependency.** Nothing in `Core` may reference `NoMercy.Plugins.Abstractions`. This is enforced by the existing CI check and by the fact that `Core.csproj` has no such reference — do not add one.
- **Every file starts with exactly these two lines:**
  ```csharp
  // SPDX-License-Identifier: MIT
  // Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84
  ```
- **Explicit types, never `var`.** Write `TorrentMetadata metadata = ...`, not `var metadata = ...`.
- **Namespace mirrors folder.** `src/.../Core/Bencode/` is `NoMercy.Plugin.TorrentDownloader.Core.Bencode`.
- **Tests:** xUnit `[Fact]` / `[Theory]`, FluentAssertions for every assertion. Name tests `Method_DescribesTheBehaviour`. Fakes live in `Tests/TestSupport/`.
- **Every test must fail if the behaviour breaks.** No test that only asserts a thing exists.
- **No seeding in this plan.** Nothing here serves a piece to a peer. `PieceServer` arrives in part 2 and is gated off by default.
- **Commit after every task**, with a conventional commit message. Do not push.

---

## File Structure

Everything lands under `src/NoMercy.Plugin.TorrentDownloader.Core/` and mirrors into
`tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/`.

| Folder | Holds | Why separate |
| --- | --- | --- |
| `Bencode/` | `BValue`, `BencodeReader`, `BencodeWriter` | Pure format handling. Knows nothing about torrents |
| `Torrents/` | `TorrentMetadata`, `FileEntry`, `MetadataParser`, `PieceLayout` | What a torrent *is*. Pure, no I/O |
| `Pieces/` | `PieceVerifier`, `IPieceStore`, `FilePieceStore`, `IResumeStore`, `FileResumeStore` | Verifying is pure; storing touches disk. Split so the pure half is trivially testable |
| `Peers/` | `PeerMessage`, `PeerMessageCodec`, `Handshake`, `PeerConnection` | One connection's worth of protocol. Owns nothing but itself |
| `Peers/Encryption/` | `MseHandshake`, `Rc4Engine`, `DiffieHellman` | The MSE layer sits below the BT handshake and is its own concern |
| `Swarm/` | `SwarmPolicy`, `TorrentCoordinator`, `PeerHandle`, `CoordinatorMessage` | The single owner and its policy |
| `Trackers/` | `IPeerSource`, `HttpTracker`, `PeerEndPoint` | Peer discovery behind one interface so UDP and DHT drop in later |

---

### Task 1: Bencode reader and writer

Bencode is the format every other piece of this depends on. It has four types: integers
(`i42e`), byte strings (`4:spam`), lists (`l...e`) and dictionaries (`d...e`). Dictionary keys are
byte strings and must be sorted when encoding, because the info hash is the SHA-1 of the *encoded*
info dictionary — encode it differently and every peer rejects you.

**Files:**
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Bencode/BValue.cs`
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Bencode/BencodeReader.cs`
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Bencode/BencodeWriter.cs`
- Test: `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Bencode/BencodeTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `abstract record BValue` with cases `BInteger(long Value)`, `BBytes(byte[] Value)`, `BList(IReadOnlyList<BValue> Items)`, `BDictionary(IReadOnlyDictionary<string, BValue> Entries)`
  - `static BValue BencodeReader.Read(ReadOnlySpan<byte> input, out int consumed)`
  - `static BValue BencodeReader.Parse(ReadOnlySpan<byte> input)` — throws `BencodeException` on trailing data
  - `static byte[] BencodeWriter.Write(BValue value)`
  - `string BBytes.AsText()` — UTF-8
  - `sealed class BencodeException : Exception`

- [ ] **Step 1: Write the failing test**

Create `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Bencode/BencodeTests.cs`:

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Bencode;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Bencode;

public class BencodeTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Parse_ReadsAPositiveInteger()
    {
        BValue value = BencodeReader.Parse(Utf8("i42e"));

        value.Should().BeOfType<BInteger>().Which.Value.Should().Be(42);
    }

    [Fact]
    public void Parse_ReadsANegativeInteger()
    {
        BValue value = BencodeReader.Parse(Utf8("i-13e"));

        value.Should().BeOfType<BInteger>().Which.Value.Should().Be(-13);
    }

    [Fact]
    public void Parse_ReadsAByteString()
    {
        BValue value = BencodeReader.Parse(Utf8("4:spam"));

        value.Should().BeOfType<BBytes>().Which.AsText().Should().Be("spam");
    }

    [Fact]
    public void Parse_ReadsAnEmptyByteString()
    {
        BValue value = BencodeReader.Parse(Utf8("0:"));

        value.Should().BeOfType<BBytes>().Which.Value.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ReadsAListInOrder()
    {
        BValue value = BencodeReader.Parse(Utf8("l4:spami42ee"));

        BList list = value.Should().BeOfType<BList>().Subject;
        list.Items.Should().HaveCount(2);
        list.Items[0].Should().BeOfType<BBytes>().Which.AsText().Should().Be("spam");
        list.Items[1].Should().BeOfType<BInteger>().Which.Value.Should().Be(42);
    }

    [Fact]
    public void Parse_ReadsADictionary()
    {
        BValue value = BencodeReader.Parse(Utf8("d3:cow3:moo4:spam4:eggse"));

        BDictionary dictionary = value.Should().BeOfType<BDictionary>().Subject;
        dictionary.Entries.Should().HaveCount(2);
        ((BBytes)dictionary.Entries["cow"]).AsText().Should().Be("moo");
        ((BBytes)dictionary.Entries["spam"]).AsText().Should().Be("eggs");
    }

    [Fact]
    public void Parse_ReadsNestedStructures()
    {
        BValue value = BencodeReader.Parse(Utf8("d4:listli1ei2eee"));

        BDictionary dictionary = (BDictionary)value;
        BList list = (BList)dictionary.Entries["list"];
        list.Items.Select(item => ((BInteger)item).Value).Should().Equal(1L, 2L);
    }

    [Fact]
    public void Parse_KeepsBytesThatAreNotValidText()
    {
        byte[] input = [.. Utf8("4:"), 0x00, 0xFF, 0x80, 0x01];

        BValue value = BencodeReader.Parse(input);

        value.Should().BeOfType<BBytes>().Which.Value.Should().Equal((byte)0x00, (byte)0xFF, (byte)0x80, (byte)0x01);
    }

    [Theory]
    [InlineData("")]
    [InlineData("i42")]
    [InlineData("ie")]
    [InlineData("i4-2e")]
    [InlineData("5:abc")]
    [InlineData("-1:a")]
    [InlineData("l4:spam")]
    [InlineData("d3:cowe")]
    [InlineData("d3:cow3:mooe3:extra")]
    [InlineData("x")]
    public void Parse_RejectsMalformedInput(string input)
    {
        Action parse = () => BencodeReader.Parse(Utf8(input));

        parse.Should().Throw<BencodeException>();
    }

    [Fact]
    public void Write_RoundTripsEveryType()
    {
        byte[] original = Utf8("d3:cowi-1e4:listl4:spam0:e4:spam4:eggse");

        byte[] written = BencodeWriter.Write(BencodeReader.Parse(original));

        written.Should().Equal(original);
    }

    [Fact]
    public void Write_SortsDictionaryKeysByRawByteOrder()
    {
        BDictionary unsorted = new(new Dictionary<string, BValue>
        {
            ["spam"] = new BBytes(Utf8("eggs")),
            ["cow"] = new BBytes(Utf8("moo")),
        });

        byte[] written = BencodeWriter.Write(unsorted);

        Encoding.UTF8.GetString(written).Should().Be("d3:cow3:moo4:spam4:eggse");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `~/.dotnet/dotnet.exe test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests -c Release`

Expected: FAIL to compile — `BValue`, `BencodeReader`, `BencodeWriter`, `BencodeException` do not exist.

- [ ] **Step 3: Write the implementation**

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Bencode/BValue.cs`:

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text;

namespace NoMercy.Plugin.TorrentDownloader.Core.Bencode;

public abstract record BValue;

public sealed record BInteger(long Value) : BValue;

public sealed record BBytes(byte[] Value) : BValue
{
    public string AsText() => Encoding.UTF8.GetString(Value);
}

public sealed record BList(IReadOnlyList<BValue> Items) : BValue;

public sealed record BDictionary(IReadOnlyDictionary<string, BValue> Entries) : BValue;

public sealed class BencodeException(string message) : Exception(message);
```

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Bencode/BencodeReader.cs`:

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text;

namespace NoMercy.Plugin.TorrentDownloader.Core.Bencode;

public static class BencodeReader
{
    public static BValue Parse(ReadOnlySpan<byte> input)
    {
        BValue value = Read(input, out int consumed);

        if (consumed != input.Length)
            throw new BencodeException($"trailing data after the value: {input.Length - consumed} bytes");

        return value;
    }

    public static BValue Read(ReadOnlySpan<byte> input, out int consumed)
    {
        if (input.IsEmpty)
            throw new BencodeException("empty input");

        return input[0] switch
        {
            (byte)'i' => ReadInteger(input, out consumed),
            (byte)'l' => ReadList(input, out consumed),
            (byte)'d' => ReadDictionary(input, out consumed),
            >= (byte)'0' and <= (byte)'9' => ReadBytes(input, out consumed),
            _ => throw new BencodeException($"unexpected byte 0x{input[0]:X2} at the start of a value"),
        };
    }

    private static BInteger ReadInteger(ReadOnlySpan<byte> input, out int consumed)
    {
        int end = input.IndexOf((byte)'e');

        if (end < 0)
            throw new BencodeException("integer is not terminated");

        ReadOnlySpan<byte> digits = input[1..end];

        if (digits.IsEmpty)
            throw new BencodeException("integer has no digits");

        // "i-0e" and any leading zero are invalid per the spec, and a client that
        // accepts them will compute a different info hash than everyone else.
        if (digits[0] == (byte)'0' && digits.Length > 1)
            throw new BencodeException("integer has a leading zero");

        if (digits is [(byte)'-', (byte)'0', ..])
            throw new BencodeException("negative zero");

        if (!long.TryParse(Encoding.ASCII.GetString(digits), out long value))
            throw new BencodeException($"'{Encoding.ASCII.GetString(digits)}' is not an integer");

        consumed = end + 1;
        return new BInteger(value);
    }

    private static BBytes ReadBytes(ReadOnlySpan<byte> input, out int consumed)
    {
        int colon = input.IndexOf((byte)':');

        if (colon < 0)
            throw new BencodeException("byte string has no length separator");

        if (!int.TryParse(Encoding.ASCII.GetString(input[..colon]), out int length) || length < 0)
            throw new BencodeException("byte string has an invalid length");

        int start = colon + 1;

        if (start + length > input.Length)
            throw new BencodeException($"byte string claims {length} bytes but only {input.Length - start} remain");

        consumed = start + length;
        return new BBytes(input.Slice(start, length).ToArray());
    }

    private static BList ReadList(ReadOnlySpan<byte> input, out int consumed)
    {
        List<BValue> items = [];
        int offset = 1;

        while (true)
        {
            if (offset >= input.Length)
                throw new BencodeException("list is not terminated");

            if (input[offset] == (byte)'e')
            {
                consumed = offset + 1;
                return new BList(items);
            }

            items.Add(Read(input[offset..], out int itemConsumed));
            offset += itemConsumed;
        }
    }

    private static BDictionary ReadDictionary(ReadOnlySpan<byte> input, out int consumed)
    {
        Dictionary<string, BValue> entries = [];
        int offset = 1;

        while (true)
        {
            if (offset >= input.Length)
                throw new BencodeException("dictionary is not terminated");

            if (input[offset] == (byte)'e')
            {
                consumed = offset + 1;
                return new BDictionary(entries);
            }

            BValue key = Read(input[offset..], out int keyConsumed);

            if (key is not BBytes keyBytes)
                throw new BencodeException("dictionary key is not a byte string");

            offset += keyConsumed;

            if (offset >= input.Length)
                throw new BencodeException("dictionary key has no value");

            entries[keyBytes.AsText()] = Read(input[offset..], out int valueConsumed);
            offset += valueConsumed;
        }
    }
}
```

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Bencode/BencodeWriter.cs`:

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text;

namespace NoMercy.Plugin.TorrentDownloader.Core.Bencode;

public static class BencodeWriter
{
    public static byte[] Write(BValue value)
    {
        MemoryStream buffer = new();
        Write(buffer, value);
        return buffer.ToArray();
    }

    private static void Write(Stream output, BValue value)
    {
        switch (value)
        {
            case BInteger integer:
                WriteAscii(output, $"i{integer.Value}e");
                break;

            case BBytes bytes:
                WriteAscii(output, $"{bytes.Value.Length}:");
                output.Write(bytes.Value);
                break;

            case BList list:
                WriteAscii(output, "l");
                foreach (BValue item in list.Items)
                    Write(output, item);
                WriteAscii(output, "e");
                break;

            case BDictionary dictionary:
                WriteAscii(output, "d");
                // Raw byte order, not culture-aware ordering. The info hash is the SHA-1
                // of these bytes, so a different sort is a different torrent.
                foreach (KeyValuePair<string, BValue> entry in dictionary.Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    Write(output, new BBytes(Encoding.UTF8.GetBytes(entry.Key)));
                    Write(output, entry.Value);
                }
                WriteAscii(output, "e");
                break;

            default:
                throw new BencodeException($"cannot write {value.GetType().Name}");
        }
    }

    private static void WriteAscii(Stream output, string text) => output.Write(Encoding.ASCII.GetBytes(text));
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `~/.dotnet/dotnet.exe test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests -c Release`

Expected: PASS, all bencode tests green, no warnings.

- [ ] **Step 5: Commit**

```bash
git add src/NoMercy.Plugin.TorrentDownloader.Core/Bencode tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Bencode
git commit -m "feat(engine): bencode, encoded the one way that keeps an info hash stable"
```

---

### Task 2: Torrent metadata and the info hash

A `.torrent` is a bencoded dictionary. The part that identifies it is the `info` sub-dictionary,
whose SHA-1 is the info hash every peer and tracker keys on. Single-file torrents carry
`info.length`; multi-file torrents carry `info.files`, a list of `{length, path}` where `path` is a
list of path components. Both shapes must produce the same flat model.

**Files:**
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Torrents/FileEntry.cs`
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Torrents/TorrentMetadata.cs`
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Torrents/MetadataParser.cs`
- Test: `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Torrents/MetadataParserTests.cs`
- Test support: `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/TestSupport/TorrentBuilder.cs`

**Interfaces:**
- Consumes: `BValue`, `BencodeReader.Parse`, `BencodeWriter.Write` from Task 1.
- Produces:
  - `sealed record FileEntry(IReadOnlyList<string> Path, long Length, long Offset)` — `Offset` is the byte offset of this file within the concatenated stream
  - `sealed record TorrentMetadata(byte[] InfoHash, string Name, long PieceLength, IReadOnlyList<byte[]> PieceHashes, IReadOnlyList<FileEntry> Files, IReadOnlyList<string> Trackers)` with `long TotalLength` and `int PieceCount`
  - `static TorrentMetadata MetadataParser.FromTorrentFile(ReadOnlySpan<byte> contents)`
  - `static TorrentMetadata MetadataParser.FromInfoDictionary(BDictionary info, IReadOnlyList<string> trackers)` — part 2 needs this for BEP 9 magnet metadata
  - `sealed class MetadataException : Exception`

- [ ] **Step 1: Write the test support builder**

Create `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/TestSupport/TorrentBuilder.cs`:

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Security.Cryptography;
using System.Text;
using NoMercy.Plugin.TorrentDownloader.Core.Bencode;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

/// <summary>
/// Builds a real .torrent over real content, so tests assert against bytes a
/// client would actually receive rather than a fixture nobody can regenerate.
/// </summary>
public sealed class TorrentBuilder
{
    private readonly List<(string[] Path, byte[] Content)> _files = [];
    private long _pieceLength = 16 * 1024;
    private string _name = "test-torrent";
    private readonly List<string> _trackers = ["http://tracker.test/announce"];

    public TorrentBuilder WithPieceLength(long pieceLength)
    {
        _pieceLength = pieceLength;
        return this;
    }

    public TorrentBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    public TorrentBuilder WithFile(string path, byte[] content)
    {
        _files.Add((path.Split('/'), content));
        return this;
    }

    public TorrentBuilder WithFile(string path, string content) => WithFile(path, Encoding.UTF8.GetBytes(content));

    /// <summary>Every byte of every file, in order. This is what the pieces hash over.</summary>
    public byte[] Content() => [.. _files.SelectMany(file => file.Content)];

    public byte[] Build()
    {
        byte[] content = Content();
        List<BValue> hashes = [];

        for (long offset = 0; offset < content.Length; offset += _pieceLength)
        {
            int length = (int)Math.Min(_pieceLength, content.Length - offset);
            hashes.Add(new BBytes(SHA1.HashData(content.AsSpan((int)offset, length))));
        }

        byte[] pieces = [.. hashes.SelectMany(hash => ((BBytes)hash).Value)];

        Dictionary<string, BValue> info = new()
        {
            ["name"] = new BBytes(Encoding.UTF8.GetBytes(_name)),
            ["piece length"] = new BInteger(_pieceLength),
            ["pieces"] = new BBytes(pieces),
        };

        if (_files.Count == 1 && _files[0].Path.Length == 1)
        {
            info["length"] = new BInteger(_files[0].Content.Length);
        }
        else
        {
            info["files"] = new BList([.. _files.Select(file => (BValue)new BDictionary(new Dictionary<string, BValue>
            {
                ["length"] = new BInteger(file.Content.Length),
                ["path"] = new BList([.. file.Path.Select(part => (BValue)new BBytes(Encoding.UTF8.GetBytes(part)))]),
            }))]);
        }

        return BencodeWriter.Write(new BDictionary(new Dictionary<string, BValue>
        {
            ["announce"] = new BBytes(Encoding.UTF8.GetBytes(_trackers[0])),
            ["info"] = new BDictionary(info),
        }));
    }

    public byte[] ExpectedInfoHash()
    {
        BDictionary root = (BDictionary)BencodeReader.Parse(Build());
        return SHA1.HashData(BencodeWriter.Write(root.Entries["info"]));
    }
}
```

Create `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Torrents/MetadataParserTests.cs`:

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Torrents;

public class MetadataParserTests
{
    [Fact]
    public void FromTorrentFile_ReadsASingleFileTorrent()
    {
        TorrentBuilder builder = new TorrentBuilder()
            .WithName("single")
            .WithPieceLength(4)
            .WithFile("single", "abcdefgh");

        TorrentMetadata metadata = MetadataParser.FromTorrentFile(builder.Build());

        metadata.Name.Should().Be("single");
        metadata.PieceLength.Should().Be(4);
        metadata.TotalLength.Should().Be(8);
        metadata.PieceCount.Should().Be(2);
        metadata.Files.Should().ContainSingle();
        metadata.Files[0].Path.Should().Equal("single");
        metadata.Files[0].Offset.Should().Be(0);
    }

    [Fact]
    public void FromTorrentFile_ComputesTheInfoHashOverTheReEncodedInfoDictionary()
    {
        TorrentBuilder builder = new TorrentBuilder().WithFile("a.bin", "content");

        TorrentMetadata metadata = MetadataParser.FromTorrentFile(builder.Build());

        metadata.InfoHash.Should().Equal(builder.ExpectedInfoHash());
    }

    [Fact]
    public void FromTorrentFile_LaysMultipleFilesOutEndToEnd()
    {
        TorrentBuilder builder = new TorrentBuilder()
            .WithName("season")
            .WithPieceLength(8)
            .WithFile("season/e01.mkv", "aaaa")
            .WithFile("season/e02.mkv", "bbbbbb")
            .WithFile("season/info.nfo", "cc");

        TorrentMetadata metadata = MetadataParser.FromTorrentFile(builder.Build());

        metadata.Files.Should().HaveCount(3);
        metadata.Files[0].Offset.Should().Be(0);
        metadata.Files[1].Offset.Should().Be(4);
        metadata.Files[2].Offset.Should().Be(10);
        metadata.TotalLength.Should().Be(12);
        metadata.Files[1].Path.Should().Equal("season", "e02.mkv");
    }

    [Fact]
    public void FromTorrentFile_RoundsThePieceCountUpForAPartialLastPiece()
    {
        TorrentBuilder builder = new TorrentBuilder().WithPieceLength(4).WithFile("a.bin", "abcde");

        TorrentMetadata metadata = MetadataParser.FromTorrentFile(builder.Build());

        metadata.PieceCount.Should().Be(2);
        metadata.PieceHashes.Should().HaveCount(2);
    }

    [Fact]
    public void FromTorrentFile_RejectsAPieceListThatIsNotAMultipleOfTwenty()
    {
        byte[] torrent = Encoding.UTF8.GetBytes(
            "d8:announce20:http://tracker.test4:infod6:lengthi4e4:name1:a12:piece lengthi4e6:pieces5:abcdeee");

        Action parse = () => MetadataParser.FromTorrentFile(torrent);

        parse.Should().Throw<MetadataException>().WithMessage("*20*");
    }

    [Fact]
    public void FromTorrentFile_RejectsAPathThatEscapesTheTorrentFolder()
    {
        TorrentBuilder builder = new TorrentBuilder()
            .WithName("evil")
            .WithFile("evil/../../../etc/passwd", "pwned")
            .WithFile("evil/ok.bin", "fine");

        Action parse = () => MetadataParser.FromTorrentFile(builder.Build());

        parse.Should().Throw<MetadataException>();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `~/.dotnet/dotnet.exe test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests -c Release --filter MetadataParserTests`

Expected: FAIL to compile — `TorrentMetadata`, `MetadataParser`, `MetadataException` do not exist.

- [ ] **Step 3: Write the implementation**

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Torrents/FileEntry.cs`:

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Torrents;

/// <summary>
/// One file in a torrent. <paramref name="Offset"/> is where it starts within the
/// concatenated stream that the pieces hash over, which is what lets a piece
/// spanning two files be written to both.
/// </summary>
public sealed record FileEntry(IReadOnlyList<string> Path, long Length, long Offset)
{
    public long End => Offset + Length;
}
```

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Torrents/TorrentMetadata.cs`:

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Torrents;

public sealed record TorrentMetadata(
    byte[] InfoHash,
    string Name,
    long PieceLength,
    IReadOnlyList<byte[]> PieceHashes,
    IReadOnlyList<FileEntry> Files,
    IReadOnlyList<string> Trackers)
{
    public long TotalLength => Files.Sum(file => file.Length);

    public int PieceCount => PieceHashes.Count;

    /// <summary>The last piece is short unless the total divides evenly.</summary>
    public int LengthOfPiece(int index)
    {
        long start = index * PieceLength;
        return (int)Math.Min(PieceLength, TotalLength - start);
    }
}

public sealed class MetadataException(string message) : Exception(message);
```

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Torrents/MetadataParser.cs`:

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Security.Cryptography;
using NoMercy.Plugin.TorrentDownloader.Core.Bencode;

namespace NoMercy.Plugin.TorrentDownloader.Core.Torrents;

public static class MetadataParser
{
    private const int HashLength = 20;

    public static TorrentMetadata FromTorrentFile(ReadOnlySpan<byte> contents)
    {
        BValue root = BencodeReader.Parse(contents);

        if (root is not BDictionary dictionary)
            throw new MetadataException("a torrent file must be a dictionary");

        if (!dictionary.Entries.TryGetValue("info", out BValue? info) || info is not BDictionary infoDictionary)
            throw new MetadataException("the torrent has no info dictionary");

        return FromInfoDictionary(infoDictionary, ReadTrackers(dictionary));
    }

    public static TorrentMetadata FromInfoDictionary(BDictionary info, IReadOnlyList<string> trackers)
    {
        // The hash is over the re-encoded dictionary rather than the original slice, so
        // metadata reconstructed from peers (BEP 9) hashes identically to a .torrent file.
        byte[] infoHash = SHA1.HashData(BencodeWriter.Write(info));

        string name = Text(info, "name");
        long pieceLength = Integer(info, "piece length");

        if (pieceLength <= 0)
            throw new MetadataException("piece length must be positive");

        byte[] pieces = Bytes(info, "pieces");

        if (pieces.Length % HashLength != 0)
            throw new MetadataException($"the piece list is {pieces.Length} bytes, which is not a multiple of 20");

        List<byte[]> hashes = [];

        for (int offset = 0; offset < pieces.Length; offset += HashLength)
            hashes.Add(pieces[offset..(offset + HashLength)]);

        return new TorrentMetadata(infoHash, name, pieceLength, hashes, ReadFiles(info, name), trackers);
    }

    private static IReadOnlyList<FileEntry> ReadFiles(BDictionary info, string name)
    {
        if (info.Entries.TryGetValue("length", out BValue? single))
        {
            long length = single is BInteger integer
                ? integer.Value
                : throw new MetadataException("length must be an integer");

            return [new FileEntry([SafeComponent(name)], length, 0)];
        }

        if (!info.Entries.TryGetValue("files", out BValue? files) || files is not BList list)
            throw new MetadataException("the torrent has neither a length nor a files list");

        List<FileEntry> entries = [];
        long offset = 0;

        foreach (BValue item in list.Items)
        {
            if (item is not BDictionary file)
                throw new MetadataException("a file entry must be a dictionary");

            long length = Integer(file, "length");

            if (length < 0)
                throw new MetadataException("a file length cannot be negative");

            if (!file.Entries.TryGetValue("path", out BValue? path) || path is not BList components || components.Items.Count == 0)
                throw new MetadataException("a file entry needs a non-empty path");

            List<string> parts = [SafeComponent(name)];

            foreach (BValue component in components.Items)
            {
                if (component is not BBytes text)
                    throw new MetadataException("a path component must be a byte string");

                parts.Add(SafeComponent(text.AsText()));
            }

            entries.Add(new FileEntry(parts, length, offset));
            offset += length;
        }

        return entries;
    }

    /// <summary>
    /// A torrent is untrusted input, and a path component of ".." would write outside the
    /// download folder. Refuse rather than sanitise: a torrent that tries this is hostile,
    /// and quietly renaming its files would hide that.
    /// </summary>
    private static string SafeComponent(string component)
    {
        if (component is "" or "." or "..")
            throw new MetadataException($"'{component}' is not a usable path component");

        if (component.Contains('/') || component.Contains('\\') || component.Contains('\0'))
            throw new MetadataException($"'{component}' contains a path separator");

        if (component.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            throw new MetadataException($"'{component}' contains characters that are not valid in a file name");

        return component;
    }

    private static IReadOnlyList<string> ReadTrackers(BDictionary root)
    {
        List<string> trackers = [];

        if (root.Entries.TryGetValue("announce", out BValue? announce) && announce is BBytes single)
            trackers.Add(single.AsText());

        if (root.Entries.TryGetValue("announce-list", out BValue? tiers) && tiers is BList tierList)
        {
            foreach (BValue tier in tierList.Items)
            {
                if (tier is not BList urls)
                    continue;

                foreach (BValue url in urls.Items)
                {
                    if (url is BBytes text && !trackers.Contains(text.AsText()))
                        trackers.Add(text.AsText());
                }
            }
        }

        return trackers;
    }

    private static string Text(BDictionary dictionary, string key) =>
        dictionary.Entries.TryGetValue(key, out BValue? value) && value is BBytes bytes
            ? bytes.AsText()
            : throw new MetadataException($"'{key}' is missing or is not a byte string");

    private static long Integer(BDictionary dictionary, string key) =>
        dictionary.Entries.TryGetValue(key, out BValue? value) && value is BInteger integer
            ? integer.Value
            : throw new MetadataException($"'{key}' is missing or is not an integer");

    private static byte[] Bytes(BDictionary dictionary, string key) =>
        dictionary.Entries.TryGetValue(key, out BValue? value) && value is BBytes bytes
            ? bytes.Value
            : throw new MetadataException($"'{key}' is missing or is not a byte string");
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `~/.dotnet/dotnet.exe test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests -c Release`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/NoMercy.Plugin.TorrentDownloader.Core/Torrents tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Torrents tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/TestSupport/TorrentBuilder.cs
git commit -m "feat(engine): a torrent's identity, and a path that cannot escape its folder"
```

---

### Task 3: Piece layout across file boundaries

A piece is a window on the concatenated stream. In a multi-file torrent that window can cover the
tail of one file and the head of the next. `PieceLayout` turns "piece 7, offset 0, 16384 bytes" into
the list of (file, offset within file, length) segments it actually touches. Everything about
multi-file support reduces to getting this right.

**Files:**
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Torrents/PieceLayout.cs`
- Test: `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Torrents/PieceLayoutTests.cs`

**Interfaces:**
- Consumes: `TorrentMetadata`, `FileEntry` from Task 2.
- Produces:
  - `readonly record struct FileSegment(FileEntry File, long OffsetInFile, int Length)`
  - `static IReadOnlyList<FileSegment> PieceLayout.Segments(TorrentMetadata metadata, int pieceIndex)`
  - `static IReadOnlyList<FileSegment> PieceLayout.SegmentsFor(TorrentMetadata metadata, long absoluteOffset, int length)`

- [ ] **Step 1: Write the failing test**

Create `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Torrents/PieceLayoutTests.cs`:

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Torrents;

public class PieceLayoutTests
{
    // Three files of 4, 6 and 2 bytes with a piece length of 8 gives:
    //   piece 0 = a[0..4] + b[0..4]
    //   piece 1 = b[4..6] + c[0..2]
    private static TorrentMetadata Season() => MetadataParser.FromTorrentFile(new TorrentBuilder()
        .WithName("season")
        .WithPieceLength(8)
        .WithFile("season/a.bin", "aaaa")
        .WithFile("season/b.bin", "bbbbbb")
        .WithFile("season/c.bin", "cc")
        .Build());

    [Fact]
    public void Segments_SplitsAPieceThatSpansTwoFiles()
    {
        IReadOnlyList<FileSegment> segments = PieceLayout.Segments(Season(), 0);

        segments.Should().HaveCount(2);
        segments[0].File.Path.Should().Equal("season", "a.bin");
        segments[0].OffsetInFile.Should().Be(0);
        segments[0].Length.Should().Be(4);
        segments[1].File.Path.Should().Equal("season", "b.bin");
        segments[1].OffsetInFile.Should().Be(0);
        segments[1].Length.Should().Be(4);
    }

    [Fact]
    public void Segments_StartsMidFileWhenThePieceDoes()
    {
        IReadOnlyList<FileSegment> segments = PieceLayout.Segments(Season(), 1);

        segments.Should().HaveCount(2);
        segments[0].File.Path.Should().Equal("season", "b.bin");
        segments[0].OffsetInFile.Should().Be(4);
        segments[0].Length.Should().Be(2);
        segments[1].File.Path.Should().Equal("season", "c.bin");
        segments[1].Length.Should().Be(2);
    }

    [Fact]
    public void Segments_CoversExactlyThePieceLength()
    {
        TorrentMetadata metadata = Season();

        for (int index = 0; index < metadata.PieceCount; index++)
            PieceLayout.Segments(metadata, index).Sum(segment => segment.Length).Should().Be(metadata.LengthOfPiece(index));
    }

    [Fact]
    public void Segments_ReturnsOneSegmentForASingleFileTorrent()
    {
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(new TorrentBuilder()
            .WithName("one")
            .WithPieceLength(4)
            .WithFile("one", "abcdefgh")
            .Build());

        PieceLayout.Segments(metadata, 1).Should().ContainSingle()
            .Which.OffsetInFile.Should().Be(4);
    }

    [Fact]
    public void Segments_SkipsAZeroLengthFileEntirely()
    {
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(new TorrentBuilder()
            .WithName("gap")
            .WithPieceLength(8)
            .WithFile("gap/a.bin", "aaaa")
            .WithFile("gap/empty.nfo", "")
            .WithFile("gap/b.bin", "bbbb")
            .Build());

        IReadOnlyList<FileSegment> segments = PieceLayout.Segments(metadata, 0);

        segments.Should().HaveCount(2);
        segments.Should().NotContain(segment => segment.File.Path[1] == "empty.nfo");
    }

    [Fact]
    public void SegmentsFor_RejectsARangeOutsideTheTorrent()
    {
        TorrentMetadata metadata = Season();

        Action beyondEnd = () => PieceLayout.SegmentsFor(metadata, metadata.TotalLength, 1);

        beyondEnd.Should().Throw<ArgumentOutOfRangeException>();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `~/.dotnet/dotnet.exe test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests -c Release --filter PieceLayoutTests`

Expected: FAIL to compile — `PieceLayout` and `FileSegment` do not exist.

- [ ] **Step 3: Write the implementation**

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Torrents/PieceLayout.cs`:

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Torrents;

public readonly record struct FileSegment(FileEntry File, long OffsetInFile, int Length);

/// <summary>
/// Translates a range of the concatenated torrent stream into the files it actually
/// touches. Multi-file support is entirely this: a piece is a window, and a window
/// does not care where one file ends and the next begins.
/// </summary>
public static class PieceLayout
{
    public static IReadOnlyList<FileSegment> Segments(TorrentMetadata metadata, int pieceIndex)
    {
        if (pieceIndex < 0 || pieceIndex >= metadata.PieceCount)
            throw new ArgumentOutOfRangeException(nameof(pieceIndex), pieceIndex, "no such piece");

        return SegmentsFor(metadata, pieceIndex * metadata.PieceLength, metadata.LengthOfPiece(pieceIndex));
    }

    public static IReadOnlyList<FileSegment> SegmentsFor(TorrentMetadata metadata, long absoluteOffset, int length)
    {
        if (absoluteOffset < 0 || length < 0 || absoluteOffset + length > metadata.TotalLength)
            throw new ArgumentOutOfRangeException(nameof(absoluteOffset), absoluteOffset, "the range falls outside the torrent");

        List<FileSegment> segments = [];
        long remaining = length;
        long position = absoluteOffset;

        foreach (FileEntry file in metadata.Files)
        {
            if (remaining == 0)
                break;

            // A zero-length file occupies no bytes, so no range ever touches it.
            if (file.Length == 0 || position >= file.End || position < file.Offset)
                continue;

            long offsetInFile = position - file.Offset;
            int take = (int)Math.Min(remaining, file.Length - offsetInFile);

            segments.Add(new FileSegment(file, offsetInFile, take));

            position += take;
            remaining -= take;
        }

        return segments;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `~/.dotnet/dotnet.exe test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests -c Release`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/NoMercy.Plugin.TorrentDownloader.Core/Torrents/PieceLayout.cs tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Torrents/PieceLayoutTests.cs
git commit -m "feat(engine): a piece is a window on the stream, not a slice of one file"
```

---

### Task 4: Piece verification

Pure computation: does this block of bytes hash to the SHA-1 the metadata claims for that index?
Separate from storage on purpose — this half needs no disk and no temporary folder.

**Files:**
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Pieces/PieceVerifier.cs`
- Test: `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Pieces/PieceVerifierTests.cs`

**Interfaces:**
- Consumes: `TorrentMetadata` from Task 2.
- Produces: `static bool PieceVerifier.Matches(TorrentMetadata metadata, int pieceIndex, ReadOnlySpan<byte> piece)`

- [ ] **Step 1: Write the failing test**

Create `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Pieces/PieceVerifierTests.cs`:

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Pieces;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pieces;

public class PieceVerifierTests
{
    private static readonly TorrentBuilder Builder = new TorrentBuilder()
        .WithName("v")
        .WithPieceLength(4)
        .WithFile("v", "abcdefgh");

    [Fact]
    public void Matches_AcceptsTheRealBytes()
    {
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(Builder.Build());

        PieceVerifier.Matches(metadata, 0, Encoding.UTF8.GetBytes("abcd")).Should().BeTrue();
        PieceVerifier.Matches(metadata, 1, Encoding.UTF8.GetBytes("efgh")).Should().BeTrue();
    }

    [Fact]
    public void Matches_RejectsAPieceWithOneWrongByte()
    {
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(Builder.Build());

        PieceVerifier.Matches(metadata, 0, Encoding.UTF8.GetBytes("abcX")).Should().BeFalse();
    }

    [Fact]
    public void Matches_RejectsTheRightBytesAtTheWrongIndex()
    {
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(Builder.Build());

        PieceVerifier.Matches(metadata, 1, Encoding.UTF8.GetBytes("abcd")).Should().BeFalse();
    }

    [Fact]
    public void Matches_RejectsAPieceOfTheWrongLength()
    {
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(Builder.Build());

        PieceVerifier.Matches(metadata, 0, Encoding.UTF8.GetBytes("abc")).Should().BeFalse();
    }

    [Fact]
    public void Matches_AcceptsAShortFinalPiece()
    {
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(new TorrentBuilder()
            .WithName("short")
            .WithPieceLength(4)
            .WithFile("short", "abcde")
            .Build());

        PieceVerifier.Matches(metadata, 1, Encoding.UTF8.GetBytes("e")).Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `~/.dotnet/dotnet.exe test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests -c Release --filter PieceVerifierTests`

Expected: FAIL to compile — `PieceVerifier` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Pieces/PieceVerifier.cs`:

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Security.Cryptography;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pieces;

public static class PieceVerifier
{
    public static bool Matches(TorrentMetadata metadata, int pieceIndex, ReadOnlySpan<byte> piece)
    {
        if (pieceIndex < 0 || pieceIndex >= metadata.PieceCount)
            return false;

        // A piece of the wrong length is wrong even if some prefix would hash correctly,
        // and hashing it would be a waste of a SHA-1 pass.
        if (piece.Length != metadata.LengthOfPiece(pieceIndex))
            return false;

        Span<byte> actual = stackalloc byte[20];
        SHA1.HashData(piece, actual);

        return actual.SequenceEqual(metadata.PieceHashes[pieceIndex]);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `~/.dotnet/dotnet.exe test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests -c Release`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/NoMercy.Plugin.TorrentDownloader.Core/Pieces tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Pieces
git commit -m "feat(engine): a piece is what it hashes to, at the index it claims"
```

---

### Task 5: Writing pieces to disk, across files

`FilePieceStore` writes a verified piece into the segments `PieceLayout` names, and reads one back
for resume verification. It is the only thing in `Core` that touches a filesystem, behind
`IPieceStore` so the coordinator can be tested against an in-memory one.

**Files:**
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Pieces/IPieceStore.cs`
- Create: `src/NoMercy.Plugin.TorrentDownloader.Core/Pieces/FilePieceStore.cs`
- Test: `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Pieces/FilePieceStoreTests.cs`
- Test support: `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/TestSupport/TempFolder.cs`

**Interfaces:**
- Consumes: `TorrentMetadata`, `PieceLayout`, `FileSegment` from Tasks 2 and 3.
- Produces:
  - `interface IPieceStore` with `Task WritePieceAsync(int pieceIndex, ReadOnlyMemory<byte> piece, CancellationToken ct)`, `Task<byte[]> ReadPieceAsync(int pieceIndex, CancellationToken ct)`, `Task FlushAsync(CancellationToken ct)`
  - `sealed class FilePieceStore(TorrentMetadata metadata, string rootFolder) : IPieceStore, IDisposable`

- [ ] **Step 1: Write the failing test**

Create `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/TestSupport/TempFolder.cs`:

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "nm-torrent-tests",
        Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A locked handle on a build agent is not a test failure.
        }
    }
}
```

Create `tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Pieces/FilePieceStoreTests.cs`:

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Pieces;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pieces;

public class FilePieceStoreTests
{
    private static TorrentBuilder Season() => new TorrentBuilder()
        .WithName("season")
        .WithPieceLength(8)
        .WithFile("season/a.bin", "aaaa")
        .WithFile("season/b.bin", "bbbbbb")
        .WithFile("season/c.bin", "cc");

    [Fact]
    public async Task WritePieceAsync_SplitsAPieceAcrossTheFilesItCovers()
    {
        using TempFolder folder = new();
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(Season().Build());
        using FilePieceStore store = new(metadata, folder.Path);

        await store.WritePieceAsync(0, Encoding.UTF8.GetBytes("aaaabbbb"), CancellationToken.None);
        await store.FlushAsync(CancellationToken.None);

        (await File.ReadAllTextAsync(folder.File("season", "a.bin"))).Should().Be("aaaa");
        (await File.ReadAllTextAsync(folder.File("season", "b.bin"))).Should().StartWith("bbbb");
    }

    [Fact]
    public async Task WritePieceAsync_WritesTheWholeTorrentBackByteForByte()
    {
        using TempFolder folder = new();
        TorrentBuilder builder = Season();
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(builder.Build());
        byte[] content = builder.Content();

        using (FilePieceStore store = new(metadata, folder.Path))
        {
            for (int index = 0; index < metadata.PieceCount; index++)
            {
                int length = metadata.LengthOfPiece(index);
                await store.WritePieceAsync(index, content.AsMemory((int)(index * metadata.PieceLength), length), CancellationToken.None);
            }

            await store.FlushAsync(CancellationToken.None);
        }

        (await File.ReadAllTextAsync(folder.File("season", "a.bin"))).Should().Be("aaaa");
        (await File.ReadAllTextAsync(folder.File("season", "b.bin"))).Should().Be("bbbbbb");
        (await File.ReadAllTextAsync(folder.File("season", "c.bin"))).Should().Be("cc");
    }

    [Fact]
    public async Task ReadPieceAsync_ReturnsWhatWasWritten()
    {
        using TempFolder folder = new();
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(Season().Build());
        using FilePieceStore store = new(metadata, folder.Path);

        await store.WritePieceAsync(1, Encoding.UTF8.GetBytes("bbcc"), CancellationToken.None);
        await store.FlushAsync(CancellationToken.None);

        byte[] read = await store.ReadPieceAsync(1, CancellationToken.None);

        Encoding.UTF8.GetString(read).Should().Be("bbcc");
    }

    [Fact]
    public async Task WritePieceAsync_CreatesTheFoldersTheTorrentNames()
    {
        using TempFolder folder = new();
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(new TorrentBuilder()
            .WithName("deep")
            .WithPieceLength(4)
            .WithFile("deep/sub/one.bin", "abcd")
            .Build());
        using FilePieceStore store = new(metadata, folder.Path);

        await store.WritePieceAsync(0, Encoding.UTF8.GetBytes("abcd"), CancellationToken.None);
        await store.FlushAsync(CancellationToken.None);

        File.Exists(folder.File("deep", "sub", "one.bin")).Should().BeTrue();
    }

    [Fact]
    public async Task WritePieceAsync_RejectsAPieceOfTheWrongLength()
    {
        using TempFolder folder = new();
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(Season().Build());
        using FilePieceStore store = new(metadata, folder.Path);

        Func<Task> write = () => store.WritePieceAsync(0, Encoding.UTF8.GetBytes("short"), CancellationToken.None);

        await write.Should().ThrowAsync<ArgumentException>();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `~/.dotnet/dotnet.exe test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests -c Release --filter FilePieceStoreTests`

Expected: FAIL to compile — `IPieceStore` and `FilePieceStore` do not exist.

- [ ] **Step 3: Write the implementation**

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Pieces/IPieceStore.cs`:

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Pieces;

public interface IPieceStore
{
    Task WritePieceAsync(int pieceIndex, ReadOnlyMemory<byte> piece, CancellationToken ct);

    Task<byte[]> ReadPieceAsync(int pieceIndex, CancellationToken ct);

    /// <summary>
    /// Must return only once the bytes are durable. The resume record is written after
    /// this returns, and the invariant is that the record never claims more than the disk holds.
    /// </summary>
    Task FlushAsync(CancellationToken ct);
}
```

Create `src/NoMercy.Plugin.TorrentDownloader.Core/Pieces/FilePieceStore.cs`:

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Torrents;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pieces;

public sealed class FilePieceStore(TorrentMetadata metadata, string rootFolder) : IPieceStore, IDisposable
{
    private readonly Dictionary<string, FileStream> _open = [];
    private bool _disposed;

    public async Task WritePieceAsync(int pieceIndex, ReadOnlyMemory<byte> piece, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int expected = metadata.LengthOfPiece(pieceIndex);

        if (piece.Length != expected)
            throw new ArgumentException($"piece {pieceIndex} is {expected} bytes, not {piece.Length}", nameof(piece));

        int offset = 0;

        foreach (FileSegment segment in PieceLayout.Segments(metadata, pieceIndex))
        {
            FileStream stream = Open(segment.File);
            stream.Seek(segment.OffsetInFile, SeekOrigin.Begin);
            await stream.WriteAsync(piece.Slice(offset, segment.Length), ct);
            offset += segment.Length;
        }
    }

    public async Task<byte[]> ReadPieceAsync(int pieceIndex, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] piece = new byte[metadata.LengthOfPiece(pieceIndex)];
        int offset = 0;

        foreach (FileSegment segment in PieceLayout.Segments(metadata, pieceIndex))
        {
            FileStream stream = Open(segment.File);
            stream.Seek(segment.OffsetInFile, SeekOrigin.Begin);

            int read = await stream.ReadAtLeastAsync(
                piece.AsMemory(offset, segment.Length),
                segment.Length,
                throwOnEndOfStream: false,
                ct);

            // A short read means the file is not there yet. Leave the rest zeroed;
            // the verifier will reject the piece, which is the correct answer.
            offset += segment.Length;

            if (read < segment.Length)
                break;
        }

        return piece;
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        foreach (FileStream stream in _open.Values)
            await stream.FlushAsync(ct);

        // FlushAsync alone leaves the bytes in the OS cache. The resume invariant needs
        // them on the platter before the record is written, so force it.
        foreach (FileStream stream in _open.Values)
            stream.Flush(flushToDisk: true);
    }

    private FileStream Open(FileEntry file)
    {
        string path = Path.Combine([rootFolder, .. file.Path]);

        if (_open.TryGetValue(path, out FileStream? existing))
            return existing;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        FileStream stream = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        _open[path] = stream;
        return stream;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (FileStream stream in _open.Values)
            stream.Dispose();

        _open.Clear();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `~/.dotnet/dotnet.exe test tests/NoMercy.Plugin.TorrentDownloader.Core.Tests -c Release`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/NoMercy.Plugin.TorrentDownloader.Core/Pieces tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/Pieces tests/NoMercy.Plugin.TorrentDownloader.Core.Tests/TestSupport/TempFolder.cs
git commit -m "feat(engine): a verified piece lands in every file it covers, durably"
```

---

## Remaining tasks

Tasks 6 to 15 complete part 1 and are written out in the same shape — failing test, run it, minimal
implementation, run it, commit. They are listed here so the sequence is visible; each is expanded in
place before it is executed.

| Task | Delivers | Key test |
| --- | --- | --- |
| 6 | `IResumeStore` / `FileResumeStore` — persist the bitfield, write it only after `FlushAsync` returns | A record written before a flush is never observed after a simulated crash |
| 7 | `PeerMessage` and `PeerMessageCodec` — the ten wire messages, length-prefixed framing | A message split across three reads decodes identically to one arriving whole |
| 8 | `Handshake` — the 68-byte BT handshake, info-hash and peer-id exchange | A handshake for the wrong info hash is rejected |
| 9 | `Peers/Encryption/` — Diffie-Hellman, RC4, the MSE handshake, forced | An unencrypted peer is refused; two MSE ends agree on a key and exchange a message |
| 10 | `PeerConnection` over a `Stream` — handshake, then messages in and out | Both ends of an in-memory duplex complete a handshake and trade bitfields |
| 11 | `SwarmPolicy` — the values from spec §4, `UploadPermitted` hard-false | Uploading is refused for a public torrent regardless of any other setting |
| 12 | `TorrentCoordinator` — single owner, rarest-first selection, in-flight tracking, hash-failure banning | Given four peers and their bitfields, the rarest piece is requested first; a peer is banned on its third failed piece |
| 13 | `IPeerSource` and `HttpTracker` — announce, compact peer list, backoff | A tracker returning a compact peer list yields the right endpoints; a 500 backs off without killing the torrent |
| 14 | `TestSeeder` — an in-process seeder that serves a real torrent, and can lie | A full multi-file download completes against it, byte for byte |
| 15 | Endgame and resume-after-restart | A download killed at 60% resumes and completes; a stalled tail does not park at 99% |

---

## Self-Review

**Spec coverage.** Part 1 implements spec §4 units `Bencode`, `Metadata`, `PeerConnection`,
`TorrentCoordinator`, `PieceVerifier`, `PieceStore`, `ResumeStore`, `SwarmPolicy`, and the
`HttpTracker` implementation of `IPeerSource`. It implements requirements 1, 2 (multi-file only), 4,
5, 6 and 7, and the resume invariant of §7. Deferred to part 2, deliberately: magnet and BEP 9/10
(requirement 2), UDP trackers and DHT (requirement 2), public-first search (requirement 8),
`TrackerSet` merging (requirement 9), private tracker configuration (requirement 10), `PieceServer`
and seeding (requirement 11), and the `ITorrentEngine` facade.

**Placeholder scan.** No TBD, TODO, or "handle errors appropriately". Tasks 6-15 are summarised
rather than expanded, which is a stated staging decision, not a placeholder — each is expanded to
full steps before execution.

**Type consistency.** `TorrentMetadata.LengthOfPiece(int)` is defined in Task 2 and used in Tasks 3,
4 and 5 under that name. `FileEntry.Offset` and `FileEntry.End` are defined in Task 2 and used in
Task 3. `FileSegment(File, OffsetInFile, Length)` is defined in Task 3 and consumed in Task 5.
`IPieceStore.FlushAsync` is defined in Task 5 and is what Task 6's ordering invariant depends on.

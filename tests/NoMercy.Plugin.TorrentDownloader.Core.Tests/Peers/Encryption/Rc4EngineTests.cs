// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Peers.Encryption;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Peers.Encryption;

public class Rc4EngineTests
{
    // The published RC4 vectors. Getting these right is the difference between
    // a cipher and a random number generator that happens to be deterministic.
    [Theory]
    [InlineData("Key", "Plaintext", "BBF316E8D940AF0AD3")]
    [InlineData("Wiki", "pedia", "1021BF0420")]
    [InlineData("Secret", "Attack at dawn", "45A01F645FC35B383552544B9BF5")]
    public void Process_MatchesThePublishedVectors(string key, string plaintext, string expected)
    {
        Rc4Engine engine = new(Encoding.ASCII.GetBytes(key), discardBytes: 0);
        byte[] buffer = Encoding.ASCII.GetBytes(plaintext);

        engine.Process(buffer);

        Convert.ToHexString(buffer).Should().Be(expected);
    }

    [Fact]
    public void Process_IsItsOwnInverse()
    {
        byte[] key = Encoding.ASCII.GetBytes("shared-secret");
        byte[] message = Encoding.UTF8.GetBytes("the block you asked for");
        byte[] buffer = (byte[])message.Clone();

        new Rc4Engine(key, discardBytes: 1024).Process(buffer);
        buffer.Should().NotEqual(message);

        new Rc4Engine(key, discardBytes: 1024).Process(buffer);
        buffer.Should().Equal(message);
    }

    [Fact]
    public void Process_KeepsItsPlaceAcrossCalls()
    {
        byte[] key = Encoding.ASCII.GetBytes("Key");
        byte[] whole = Encoding.ASCII.GetBytes("Plaintext");

        Rc4Engine oneGo = new(key, discardBytes: 0);
        oneGo.Process(whole);

        byte[] first = Encoding.ASCII.GetBytes("Plain");
        byte[] second = Encoding.ASCII.GetBytes("text");
        Rc4Engine piecemeal = new(key, discardBytes: 0);
        piecemeal.Process(first);
        piecemeal.Process(second);

        Convert.ToHexString([.. first, .. second]).Should().Be(Convert.ToHexString(whole));
    }

    [Fact]
    public void Constructor_DiscardsTheRequestedPrefixOfTheKeystream()
    {
        byte[] key = Encoding.ASCII.GetBytes("Key");

        byte[] withoutDiscard = new byte[4];
        Rc4Engine plain = new(key, discardBytes: 0);
        plain.Process(new byte[1024]);
        plain.Process(withoutDiscard);

        byte[] withDiscard = new byte[4];
        new Rc4Engine(key, discardBytes: 1024).Process(withDiscard);

        // MSE throws away the first 1024 bytes precisely because RC4's early keystream
        // leaks key material. Discarding must land on the same place as consuming.
        withDiscard.Should().Equal(withoutDiscard);
    }

    [Fact]
    public void Constructor_RejectsAnEmptyKey()
    {
        Action empty = () => _ = new Rc4Engine([], discardBytes: 0);

        empty.Should().Throw<ArgumentException>();
    }
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Security.Cryptography;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Peers.Encryption;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Peers.Encryption;

public class MseCryptoTests
{
    private static readonly byte[] InfoHash = Enumerable.Range(0, 20).Select(value => (byte)value).ToArray();

    [Fact]
    public void PublicKey_IsAlwaysNinetySixBytes()
    {
        // The wire format has no length prefix, so a short key would silently shift
        // every byte after it. Left padding is not cosmetic here.
        for (int attempt = 0; attempt < 25; attempt++)
        {
            byte[] privateKey = MseCrypto.NewPrivateKey();

            MseCrypto.PublicKey(privateKey).Should().HaveCount(96);
        }
    }

    [Fact]
    public void SharedSecret_IsTheSameOnBothSides()
    {
        byte[] ourPrivate = MseCrypto.NewPrivateKey();
        byte[] theirPrivate = MseCrypto.NewPrivateKey();

        byte[] ours = MseCrypto.SharedSecret(ourPrivate, MseCrypto.PublicKey(theirPrivate));
        byte[] theirs = MseCrypto.SharedSecret(theirPrivate, MseCrypto.PublicKey(ourPrivate));

        ours.Should().Equal(theirs);
        ours.Should().HaveCount(96);
    }

    [Fact]
    public void NewPrivateKey_DiffersEveryTime()
    {
        MseCrypto.NewPrivateKey().Should().NotEqual(MseCrypto.NewPrivateKey());
    }

    [Fact]
    public void SharedSecret_DiffersForADifferentPeer()
    {
        byte[] ourPrivate = MseCrypto.NewPrivateKey();

        byte[] first = MseCrypto.SharedSecret(ourPrivate, MseCrypto.PublicKey(MseCrypto.NewPrivateKey()));
        byte[] second = MseCrypto.SharedSecret(ourPrivate, MseCrypto.PublicKey(MseCrypto.NewPrivateKey()));

        first.Should().NotEqual(second);
    }

    [Fact]
    public void SharedSecret_RejectsAPublicKeyOfTheWrongLength()
    {
        Action tooShort = () => MseCrypto.SharedSecret(MseCrypto.NewPrivateKey(), new byte[95]);

        tooShort.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Hash_IsSha1OverTheLabelThenTheParts()
    {
        byte[] secret = Enumerable.Repeat((byte)0x42, 96).ToArray();

        byte[] actual = MseCrypto.Hash("req1", secret);

        byte[] expected = SHA1.HashData([.. "req1"u8.ToArray(), .. secret]);
        actual.Should().Equal(expected);
    }

    [Fact]
    public void IncomingAndOutgoingKeys_AreDifferentAndSwapBetweenTheTwoEnds()
    {
        byte[] secret = Enumerable.Repeat((byte)0x7, 96).ToArray();

        byte[] initiatorOut = MseCrypto.Key(initiating: true, secret, InfoHash);
        byte[] initiatorIn = MseCrypto.Key(initiating: false, secret, InfoHash);

        // What the initiator writes with is what the receiver reads with, and vice
        // versa. One key for both directions would let a replay be its own answer.
        initiatorOut.Should().NotEqual(initiatorIn);
        initiatorOut.Should().Equal(MseCrypto.Key(initiating: true, secret, InfoHash));
    }

    [Fact]
    public void Key_DependsOnTheTorrent()
    {
        byte[] secret = Enumerable.Repeat((byte)0x7, 96).ToArray();
        byte[] otherTorrent = Enumerable.Repeat((byte)0xEE, 20).ToArray();

        MseCrypto.Key(initiating: true, secret, InfoHash)
            .Should().NotEqual(MseCrypto.Key(initiating: true, secret, otherTorrent));
    }

    [Fact]
    public void ObfuscatedHash_CombinesTheTorrentAndTheSecret()
    {
        byte[] secret = Enumerable.Repeat((byte)0x11, 96).ToArray();

        byte[] actual = MseCrypto.ObfuscatedInfoHash(secret, InfoHash);

        byte[] req2 = MseCrypto.Hash("req2", InfoHash);
        byte[] req3 = MseCrypto.Hash("req3", secret);
        byte[] expected = [.. req2.Zip(req3, (left, right) => (byte)(left ^ right))];

        actual.Should().Equal(expected);
        actual.Should().HaveCount(20);
    }
}

using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// MSE/PE, which is what most of a swarm insists on before it will say a word.
/// </summary>
/// <remarks>
/// <para>
/// Three things here are checked against something outside this repository, and
/// they are the three that would otherwise be a client talking happily to
/// itself and to nobody else: the cipher is put to RFC 6229's published RC4
/// vectors, the Diffie-Hellman prime is put to a primality test written in this
/// file rather than taken on trust, and the handshake's structure is asserted
/// on the bytes that really went over the wire.
/// </para>
/// <para>
/// The exchange between two ends is this client's initiator against this
/// client's receiver. That is worth what it is worth and no more, which is why
/// the constants above are checked independently — a mistyped prime or a
/// forgotten keystream discard would pass a round trip and fail against every
/// real peer. <c>tools/Capture --mse</c> exists for the day a peer will hold a
/// conversation.
/// </para>
/// </remarks>
public class EncryptionTests
{
    /// <remarks>
    /// RFC 6229 § 2, the forty-bit key. .NET has no RC4 and will not be getting
    /// one, so this cipher is written here — and a stream cipher written wrong
    /// produces something that looks exactly as random as one written right.
    /// </remarks>
    [Theory]
    [InlineData("0102030405", 0, "b2396305f03dc027ccc3524a0a1118a8")]
    [InlineData("0102030405", 16, "6982944f18fc82d589c403a47a0d0919")]
    [InlineData("0102030405", 240, "28cb1132c96ce286421dcaadb8b69eae")]
    [InlineData("0102030405060708", 0, "97ab8a1bf0afb96132f2f67258da15a8")]
    [InlineData("0102030405060708", 240, "9636ebc9841926f4f7d1f362bddf6e18")]
    public void TheCipherIsRc4AsRfc6229PublishedIt(string key, int offset, string expected)
    {
        // The keystream is what a cipher applied to nought comes out as.
        byte[] stream = new byte[offset + 16];

        new Rc4(Convert.FromHexString(key), discard: 0).Apply(stream);

        Assert.Equal(expected.ToUpperInvariant(), Convert.ToHexString(stream.AsSpan(offset, 16).ToArray()));
    }

    /// <remarks>
    /// The three everybody quotes, which are text rather than a keystream and
    /// so exercise the same cipher from the other side.
    /// </remarks>
    [Theory]
    [InlineData("Key", "Plaintext", "BBF316E8D940AF0AD3")]
    [InlineData("Wiki", "pedia", "1021BF0420")]
    [InlineData("Secret", "Attack at dawn", "45A01F645FC35B383552544B9BF5")]
    public void TheCipherTurnsTheWellKnownVectorsIntoTheirWellKnownCiphertext(string key, string text, string expected)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);

        new Rc4(Encoding.ASCII.GetBytes(key), discard: 0).Apply(bytes);

        Assert.Equal(expected, Convert.ToHexString(bytes));
    }

    /// <remarks>
    /// A thousand and twenty-four bytes go in the bin before anything is sent,
    /// because RC4's first bytes lean towards the key. A client that forgot
    /// would read rubbish from the very first byte — and against its own other
    /// end, which forgot in the same way, it would work perfectly.
    /// </remarks>
    [Fact]
    public void TheFirstThousandAndTwentyFourBytesOfKeystreamAreThrownAway()
    {
        Assert.Equal(1024, Mse.Discard);

        byte[] whole = new byte[Mse.Discard + 8];
        byte[] discarded = new byte[8];

        new Rc4("key"u8, discard: 0).Apply(whole);
        new Rc4("key"u8).Apply(discarded);

        Assert.Equal(whole.AsSpan(Mse.Discard, 8).ToArray(), discarded);
        Assert.NotEqual(whole[..8], discarded);
    }

    /// <remarks>
    /// <para>
    /// Every peer in the world uses this prime, and a digit typed wrong gives a
    /// shared secret nobody else can arrive at. Miller-Rabin below, on the
    /// number as this client really holds it: it has to be prime, it has to be
    /// 768 bits, and it has to be a <em>safe</em> prime, which is what MSE and
    /// RFC 2409 both say the first Oakley group is.
    /// </para>
    /// <para>
    /// A test that compared the constant with itself spelled a second way would
    /// prove nothing at all; this one would catch any single digit.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheDiffieHellmanPrimeIsTheSafePrimeMseNames()
    {
        BigInteger prime = new(Mse.Prime, isUnsigned: true, isBigEndian: true);

        Assert.Equal(96, Mse.Prime.Length);
        Assert.Equal(768, (int)prime.GetBitLength());
        Assert.True(IsPrime(prime));

        // Safe: (p-1)/2 is prime too, which is what makes the group's small
        // subgroups uninteresting.
        Assert.True(IsPrime((prime - 1) / 2));

        Assert.Equal(2, Mse.Generator);
    }

    /// <remarks>
    /// Ninety-six bytes, always. The number is smaller than that about once in
    /// two hundred and fifty, and a client that sent it without the padding
    /// would have that connection fail for no reason anybody could see.
    /// </remarks>
    [Fact]
    public void APublicKeyIsAlwaysNinetySixBytesEvenWhenTheNumberIsShorter()
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            Assert.Equal(Mse.KeyLength, MseKeyPair.Create(RandomNumberGenerator.Create()).Public.Length);
        }

        // A private key of one gives the generator itself: two, in ninety-six
        // bytes, which is ninety-five of nought and then a two. Nothing else
        // exercises the padding on purpose.
        MseKeyPair small = MseKeyPair.Create(new FixedRandom(1));

        Assert.Equal(Mse.KeyLength, small.Public.Length);
        Assert.Equal(2, small.Public[^1]);
        Assert.All(small.Public[..^1], one => Assert.Equal(0, one));
    }

    /// <remarks>
    /// The whole point of the exchange: two ends that have never met arrive at
    /// the same ninety-six bytes without either sending them.
    /// </remarks>
    [Fact]
    public void BothEndsArriveAtTheSameSecret()
    {
        MseKeyPair ours = MseKeyPair.Create(RandomNumberGenerator.Create());
        MseKeyPair theirs = MseKeyPair.Create(RandomNumberGenerator.Create());

        Assert.Equal(ours.Secret(theirs.Public), theirs.Secret(ours.Public));
        Assert.Equal(Mse.KeyLength, ours.Secret(theirs.Public).Length);

        // And a third party who did the exchange with somebody else does not.
        MseKeyPair stranger = MseKeyPair.Create(RandomNumberGenerator.Create());

        Assert.NotEqual(ours.Secret(theirs.Public), stranger.Secret(theirs.Public));
    }

    /// <remarks>
    /// A key that is not ninety-six bytes is not a key. Taking it would give a
    /// secret neither end shares and a connection that failed later, somewhere
    /// less obvious.
    /// </remarks>
    [Fact]
    public void APublicKeyOfTheWrongLengthIsRefused()
    {
        MseKeyPair ours = MseKeyPair.Create(RandomNumberGenerator.Create());

        Assert.Throws<PeerProtocolException>(() => ours.Secret(new byte[95]));
    }

    /// <remarks>
    /// The info hash never goes on the wire, obfuscated or otherwise: an
    /// observer holding a list of hashes could otherwise say which torrent a
    /// connection is for, which is most of what encrypting it was meant to
    /// prevent.
    /// </remarks>
    [Fact]
    public void WhichTorrentIsSaidWithoutSendingTheInfoHash()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(Mse.KeyLength);
        byte[] said = Mse.Req2Xor3(Ubuntu, secret);

        Assert.Equal(20, said.Length);
        Assert.False(Contains(said, Ubuntu), "the info hash is on the wire");

        // Only somebody who did the same exchange and already has the hash can
        // recognise it: another torrent's, and another connection's secret,
        // both come out as something else.
        Assert.Equal(said, Mse.Req2Xor3(Ubuntu, secret));
        Assert.NotEqual(said, Mse.Req2Xor3(Archive, secret));
        Assert.NotEqual(said, Mse.Req2Xor3(Ubuntu, RandomNumberGenerator.GetBytes(Mse.KeyLength)));
    }

    /// <remarks>
    /// The two directions are different keys on purpose. One key both ways
    /// means the same keystream both ways, and two peers sending at once would
    /// exclusive-or each other's traffic into the clear.
    /// </remarks>
    [Fact]
    public void TheTwoDirectionsUseDifferentKeys()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(Mse.KeyLength);

        Assert.NotEqual(Mse.KeyA(secret, Ubuntu), Mse.KeyB(secret, Ubuntu));

        byte[] out1 = "the same eight"u8.ToArray();
        byte[] out2 = "the same eight"u8.ToArray();

        new Rc4(Mse.KeyA(secret, Ubuntu)).Apply(out1);
        new Rc4(Mse.KeyB(secret, Ubuntu)).Apply(out2);

        Assert.NotEqual(out1, out2);
    }

    /// <remarks>
    /// End to end, with the wire recorded: the handshake goes through, the
    /// receiver works out which torrent it is for without being told, and this
    /// client's BitTorrent handshake — the words <c>BitTorrent protocol</c>,
    /// which is exactly what a router looks for — is nowhere in what was sent.
    /// </remarks>
    [Fact]
    public async Task TheHandshakeAgreesRc4AndNothingRecognisableIsOnTheWire()
    {
        PeerWire wire = new();

        Task<MseLink> dialling = MseNegotiation.InitiateAsync(
            wire.Initiator, Ubuntu, Handshake.Write(Ubuntu, PeerId), MseMethod.Plaintext | MseMethod.Rc4,
            RandomNumberGenerator.Create(), Cancellation);

        Task<MseLink> answering = MseNegotiation.AcceptAsync(
            wire.Receiver, [Archive, Ubuntu], MseMethod.Plaintext | MseMethod.Rc4,
            RandomNumberGenerator.Create(), Cancellation);

        MseLink[] both = await Task.WhenAll(dialling, answering);

        Assert.Equal(MseMethod.Rc4, both[0].Method);
        Assert.Equal(MseMethod.Rc4, both[1].Method);

        // Which torrent, worked out from a hash that is not the hash.
        Assert.Equal(Ubuntu, both[1].InfoHash);

        // And the handshake arrived, whole, on the back of the same round trip.
        Assert.Equal(Handshake.Write(Ubuntu, PeerId), both[1].Initial);

        Assert.False(Contains(wire.Sent, Handshake.Protocol.ToArray()), "the protocol name is on the wire");
        Assert.False(Contains(wire.Sent, Ubuntu), "the info hash is on the wire");
    }

    /// <remarks>
    /// What was agreed has to hold for everything after it, not just the
    /// handshake. A client whose keystreams drifted by one byte would shake
    /// hands and then read nonsense.
    /// </remarks>
    [Fact]
    public async Task WhatIsSentAfterTheHandshakeArrivesBothWays()
    {
        PeerWire wire = new();

        MseLink[] both = await Handshaken(wire);

        byte[] message = PeerMessage.Have(24207).Write();

        await both[0].Stream.WriteAsync(message, Cancellation);
        await both[0].Stream.FlushAsync(Cancellation);

        byte[] arrived = new byte[message.Length];

        await both[1].Stream.ReadExactlyAsync(arrived, Cancellation);

        Assert.Equal(message, arrived);

        // And back the other way, on the other key.
        byte[] answer = PeerMessage.Of(PeerMessageId.Unchoke).Write();

        await both[1].Stream.WriteAsync(answer, Cancellation);
        await both[1].Stream.FlushAsync(Cancellation);

        byte[] back = new byte[answer.Length];

        await both[0].Stream.ReadExactlyAsync(back, Cancellation);

        Assert.Equal(answer, back);
    }

    /// <remarks>
    /// Both are offered, always. Offering only RC4 refuses peers that will not
    /// do it, and offering only plaintext is refused by the peers that insist.
    /// </remarks>
    [Fact]
    public async Task BothMethodsAreOfferedAndPlaintextIsTakenWhenThePeerChoosesIt()
    {
        PeerWire wire = new();

        Task<MseLink> dialling = MseNegotiation.InitiateAsync(
            wire.Initiator, Ubuntu, Handshake.Write(Ubuntu, PeerId), MseMethod.Plaintext | MseMethod.Rc4,
            RandomNumberGenerator.Create(), Cancellation);

        // A peer that did the key exchange and then wants the wire in the clear.
        Task<MseLink> answering = MseNegotiation.AcceptAsync(
            wire.Receiver, [Ubuntu], MseMethod.Plaintext, RandomNumberGenerator.Create(), Cancellation);

        MseLink[] both = await Task.WhenAll(dialling, answering);

        Assert.Equal(MseMethod.Plaintext, both[0].Method);
        Assert.Equal(MseMethod.Plaintext, both[1].Method);

        // The handshake was still encrypted — it is the wire afterwards that is
        // in the clear, and that is the whole saving.
        Assert.False(Contains(wire.Sent, Handshake.Protocol.ToArray()), "the protocol name is on the wire");

        byte[] message = PeerMessage.Of(PeerMessageId.Interested).Write();

        await both[0].Stream.WriteAsync(message, Cancellation);
        await both[0].Stream.FlushAsync(Cancellation);

        byte[] arrived = new byte[message.Length];

        await both[1].Stream.ReadExactlyAsync(arrived, Cancellation);

        Assert.Equal(message, arrived);
        Assert.True(Contains(wire.Sent, message), "plaintext was agreed and the message was encrypted anyway");
    }

    /// <remarks>
    /// A peer that picks something nobody offered is a peer this client cannot
    /// read: carrying on would mean decrypting with a cipher the other end is
    /// not using. The peer here is written out by hand, because this client's
    /// own receiver would never do it.
    /// </remarks>
    [Fact]
    public async Task APeerThatChoosesAMethodNobodyOfferedIsRefused()
    {
        PeerWire wire = new();

        Task<MseLink> dialling = MseNegotiation.InitiateAsync(
            wire.Initiator, Ubuntu, Handshake.Write(Ubuntu, PeerId), MseMethod.Plaintext | MseMethod.Rc4,
            RandomNumberGenerator.Create(), Cancellation);

        await AnswerWithMethodAsync(wire, chosen: 4);

        PeerProtocolException refused = await Assert.ThrowsAsync<PeerProtocolException>(() => dialling);

        Assert.Contains("not offered", refused.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Nought is not a method either, and a client that took it at face value
    /// would go on to wrap the connection in nothing at all while the peer
    /// encrypted.
    /// </remarks>
    [Fact]
    public async Task APeerThatChoosesNothingIsRefused()
    {
        PeerWire wire = new();

        Task<MseLink> dialling = MseNegotiation.InitiateAsync(
            wire.Initiator, Ubuntu, Handshake.Write(Ubuntu, PeerId), MseMethod.Plaintext | MseMethod.Rc4,
            RandomNumberGenerator.Create(), Cancellation);

        await AnswerWithMethodAsync(wire, chosen: 0);

        await Assert.ThrowsAsync<PeerProtocolException>(() => dialling);
    }

    /// <remarks>
    /// Two ends with nothing in common: the connection is refused rather than
    /// carried on in whichever of the two the receiver happened to prefer.
    /// </remarks>
    [Fact]
    public async Task TwoEndsWithNoMethodInCommonRefuseEachOther()
    {
        PeerWire wire = new();

        Task<MseLink> dialling = MseNegotiation.InitiateAsync(
            wire.Initiator, Ubuntu, Handshake.Write(Ubuntu, PeerId), MseMethod.Rc4,
            RandomNumberGenerator.Create(), Cancellation);

        Task<MseLink> answering = MseNegotiation.AcceptAsync(
            wire.Receiver, [Ubuntu], MseMethod.Plaintext, RandomNumberGenerator.Create(), Cancellation);

        PeerProtocolException refused = await Assert.ThrowsAsync<PeerProtocolException>(() => answering);

        Assert.Contains("no method", refused.Message, StringComparison.Ordinal);

        // And the dialling end is left with a connection that will never
        // answer, which is what a refusal looks like from the other side.
        Assert.False(dialling.IsCompletedSuccessfully);
    }

    /// <remarks>
    /// The eight bytes of nought are the only proof both ends arrived at the
    /// same secret. Somebody who knows the info hash — anybody, it is in the
    /// magnet — can send the two hashes; what they cannot do without the secret
    /// is encrypt the verification constant so that it comes back out as
    /// nought.
    /// </remarks>
    [Fact]
    public async Task ADialerWhoseVerificationConstantDoesNotComeOutAsNoughtIsRefused()
    {
        PeerWire wire = new();

        Task<MseLink> answering = MseNegotiation.AcceptAsync(
            wire.Receiver, [Ubuntu], MseMethod.Rc4, RandomNumberGenerator.Create(), Cancellation);

        MseKeyPair ours = MseKeyPair.Create(RandomNumberGenerator.Create());

        await wire.Initiator.WriteAsync(ours.Public, Cancellation);
        await wire.Initiator.FlushAsync(Cancellation);

        byte[] theirs = new byte[Mse.KeyLength];

        await wire.Initiator.ReadExactlyAsync(theirs, Cancellation);

        byte[] secret = ours.Secret(theirs);

        // Both hashes right, and then fourteen bytes of something else.
        await wire.Initiator.WriteAsync(Mse.Req1(secret), Cancellation);
        await wire.Initiator.WriteAsync(Mse.Req2Xor3(Ubuntu, secret), Cancellation);
        await wire.Initiator.WriteAsync(RandomNumberGenerator.GetBytes(14), Cancellation);
        await wire.Initiator.FlushAsync(Cancellation);

        PeerProtocolException refused = await Assert.ThrowsAsync<PeerProtocolException>(() => answering);

        Assert.Contains("verification constant", refused.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A connection for a torrent this client is not holding is refused. It is
    /// also the only way the receiver can tell: the info hash is not on the
    /// wire, so a hash that matches nothing matches nothing.
    /// </remarks>
    [Fact]
    public async Task ADialForATorrentThisClientIsNotHoldingIsRefused()
    {
        PeerWire wire = new();

        Task<MseLink> dialling = MseNegotiation.InitiateAsync(
            wire.Initiator, Ubuntu, Handshake.Write(Ubuntu, PeerId), MseMethod.Rc4,
            RandomNumberGenerator.Create(), Cancellation);

        Task<MseLink> answering = MseNegotiation.AcceptAsync(
            wire.Receiver, [Archive], MseMethod.Rc4, RandomNumberGenerator.Create(), Cancellation);

        PeerProtocolException refused = await Assert.ThrowsAsync<PeerProtocolException>(() => answering);

        Assert.Contains("not holding", refused.Message, StringComparison.Ordinal);

        _ = dialling;
    }

    /// <remarks>
    /// Encrypted first, in the clear second, and the peer is used either way.
    /// A client that gave up on a peer that would not do MSE would throw away
    /// half a swarm; one that never tried it is refused by the other half.
    /// </remarks>
    [Fact]
    public async Task AnOutgoingConnectionTriesEncryptedFirstAndFallsBackToPlaintext()
    {
        List<PeerWire> attempts = [];

        // A peer of the older sort: it answers anything with its handshake.
        MseLink link = await PeerDial.ConnectAsync(
            _ =>
            {
                PeerWire wire = new();

                attempts.Add(wire);

                Task.Run(async () =>
                {
                    await wire.Receiver.WriteAsync(Handshake.Write(Ubuntu, TheirId), Cancellation);
                    await wire.Receiver.FlushAsync(Cancellation);
                });

                return Task.FromResult<Stream>(wire.Initiator);
            },
            Ubuntu,
            Handshake.Write(Ubuntu, PeerId),
            RandomNumberGenerator.Create(),
            Cancellation);

        Assert.Equal(MseMethod.Plaintext, link.Method);
        Assert.Equal(2, attempts.Count);

        // The first attempt was encrypted — ninety-six bytes of key, not a
        // handshake — and the second was the handshake itself.
        Assert.True(attempts[0].Sent.Length >= Mse.KeyLength);
        Assert.False(Contains(attempts[0].Sent, Handshake.Protocol.ToArray()));

        Assert.Equal(Handshake.Write(Ubuntu, PeerId), attempts[1].Sent);
    }

    /// <remarks>
    /// Padding is nought to five hundred and twelve bytes and its length is
    /// nowhere on the wire, so both ends find their footing by looking for
    /// something they can predict. A peer that sends more than MSE allows is
    /// one this client would otherwise read from until the connection died.
    /// </remarks>
    [Fact]
    public async Task APeerThatSendsMorePaddingThanMseAllowsIsRefused()
    {
        PeerWire wire = new();

        Task<MseLink> answering = MseNegotiation.AcceptAsync(
            wire.Receiver, [Ubuntu], MseMethod.Rc4, RandomNumberGenerator.Create(), Cancellation);

        await wire.Initiator.WriteAsync(MseKeyPair.Create(RandomNumberGenerator.Create()).Public, Cancellation);
        await wire.Initiator.WriteAsync(new byte[Mse.MostPadding + 64], Cancellation);
        await wire.Initiator.FlushAsync(Cancellation);

        PeerProtocolException refused = await Assert.ThrowsAsync<PeerProtocolException>(() => answering);

        Assert.Contains("padding", refused.Message, StringComparison.Ordinal);
    }

    private static readonly byte[] Ubuntu =
        Convert.FromHexString("D160B8D8EA35A5B4E52837468FC8F03D55CEF1F7");

    private static readonly byte[] Archive =
        Convert.FromHexString("E2720161FF77B42E61D15F4958134DEBAE8D0A96");

    private static readonly byte[] PeerId = Encoding.ASCII.GetBytes("-NM0400-000000000001");

    private static readonly byte[] TheirId = Encoding.ASCII.GetBytes("-TR4130-abcdefghijkl");

    /// <summary>
    /// A handshake that cannot hang the suite: a fault in either end shows up
    /// as this rather than as a run that never finishes.
    /// </summary>
    private static CancellationToken Cancellation => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    /// <summary>
    /// A peer of our own making that answers with whatever method it likes.
    /// </summary>
    /// <remarks>
    /// It does the key exchange properly and then chooses badly, which is the
    /// only way to put the initiator's own rule to the test — this client's
    /// receiver will never choose something that was not offered.
    /// </remarks>
    private static async Task AnswerWithMethodAsync(PeerWire wire, int chosen)
    {
        byte[] ours = new byte[Mse.KeyLength];

        await wire.Receiver.ReadExactlyAsync(ours, Cancellation);

        MseKeyPair theirs = MseKeyPair.Create(RandomNumberGenerator.Create());

        await wire.Receiver.WriteAsync(theirs.Public, Cancellation);

        byte[] secret = theirs.Secret(ours);

        byte[] answer = new byte[Mse.Verification.Length + 6];

        BinaryPrimitives.WriteInt32BigEndian(answer.AsSpan(Mse.Verification.Length), chosen);

        new Rc4(Mse.KeyB(secret, Ubuntu)).Apply(answer);

        await wire.Receiver.WriteAsync(answer, Cancellation);
        await wire.Receiver.FlushAsync(Cancellation);
    }

    /// <summary>Both ends, having agreed RC4.</summary>
    private static Task<MseLink[]> Handshaken(PeerWire wire)
    {
        return Task.WhenAll(
            MseNegotiation.InitiateAsync(
                wire.Initiator, Ubuntu, Handshake.Write(Ubuntu, PeerId), MseMethod.Plaintext | MseMethod.Rc4,
                RandomNumberGenerator.Create(), Cancellation),
            MseNegotiation.AcceptAsync(
                wire.Receiver, [Ubuntu], MseMethod.Plaintext | MseMethod.Rc4,
                RandomNumberGenerator.Create(), Cancellation));
    }

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        return haystack.IndexOf(needle) >= 0;
    }

    /// <summary>
    /// Miller-Rabin, so the prime above is checked rather than believed.
    /// </summary>
    /// <remarks>
    /// The bases are the first twelve primes, which is a deterministic test far
    /// beyond any number this size in practice and is in any case being asked
    /// about one number that either is the right one or is a typing mistake.
    /// </remarks>
    private static bool IsPrime(BigInteger number)
    {
        BigInteger odd = number - 1;
        int twos = 0;

        while (odd.IsEven)
        {
            odd /= 2;
            twos++;
        }

        foreach (int witness in (int[])[2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37])
        {
            BigInteger power = BigInteger.ModPow(witness, odd, number);

            if (power == 1 || power == number - 1)
            {
                continue;
            }

            bool composite = true;

            for (int again = 0; again < twos - 1 && composite; again++)
            {
                power = BigInteger.ModPow(power, 2, number);

                if (power == number - 1)
                {
                    composite = false;
                }
            }

            if (composite)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A private key of a known number, so the padding can be got at.</summary>
    private sealed class FixedRandom(int value) : RandomNumberGenerator
    {
        public override void GetBytes(byte[] data)
        {
            Array.Clear(data);
            data[^1] = (byte)value;
        }
    }
}

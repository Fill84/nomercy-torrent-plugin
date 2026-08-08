// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Pieces;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pieces;

public class BitfieldTests
{
    [Fact]
    public void New_StartsWithNothingSet()
    {
        Bitfield field = new(10);

        field.Length.Should().Be(10);
        field.SetCount.Should().Be(0);
        field.IsComplete.Should().BeFalse();
        field[0].Should().BeFalse();
    }

    [Fact]
    public void Set_MarksOnlyTheIndexGiven()
    {
        Bitfield field = new(10);

        field[3] = true;

        field[3].Should().BeTrue();
        field[2].Should().BeFalse();
        field[4].Should().BeFalse();
        field.SetCount.Should().Be(1);
    }

    [Fact]
    public void Set_IsIdempotentForTheCount()
    {
        Bitfield field = new(10);

        field[3] = true;
        field[3] = true;

        field.SetCount.Should().Be(1);
    }

    [Fact]
    public void Clear_ReducesTheCount()
    {
        Bitfield field = new(10);
        field[3] = true;

        field[3] = false;

        field[3].Should().BeFalse();
        field.SetCount.Should().Be(0);
    }

    [Fact]
    public void IsComplete_IsTrueOnlyWhenEveryPieceIsSet()
    {
        Bitfield field = new(3);

        field[0] = true;
        field[1] = true;
        field.IsComplete.Should().BeFalse();

        field[2] = true;
        field.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void Indexer_RejectsAnIndexOutsideTheField()
    {
        Bitfield field = new(4);

        Action read = () => _ = field[4];
        Action write = () => field[-1] = true;

        read.Should().Throw<ArgumentOutOfRangeException>();
        write.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ToBytes_PutsTheFirstPieceInTheHighBitOfTheFirstByte()
    {
        Bitfield field = new(8);
        field[0] = true;

        field.ToBytes().Should().Equal((byte)0b1000_0000);
    }

    [Fact]
    public void ToBytes_RoundsUpToWholeBytes()
    {
        Bitfield field = new(9);
        field[8] = true;

        field.ToBytes().Should().Equal((byte)0, (byte)0b1000_0000);
    }

    [Fact]
    public void FromBytes_ReadsWhatToBytesWrote()
    {
        Bitfield original = new(20);
        original[0] = true;
        original[7] = true;
        original[8] = true;
        original[19] = true;

        Bitfield restored = Bitfield.FromBytes(original.ToBytes(), 20);

        restored.SetCount.Should().Be(4);
        restored[0].Should().BeTrue();
        restored[7].Should().BeTrue();
        restored[8].Should().BeTrue();
        restored[19].Should().BeTrue();
        restored[1].Should().BeFalse();
    }

    [Fact]
    public void FromBytes_RejectsAWrongLengthPayload()
    {
        Action tooShort = () => Bitfield.FromBytes([0x00], 20);

        tooShort.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FromBytes_RejectsSpareBitsThatAreSet()
    {
        // 9 pieces occupy two bytes; the last seven bits are spare and the spec
        // requires them zero. A peer setting them is lying about what it holds.
        Action lying = () => Bitfield.FromBytes([0xFF, 0xFF], 9);

        lying.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Missing_ListsTheIndicesThatAreNotSet()
    {
        Bitfield field = new(4);
        field[1] = true;

        field.Missing().Should().Equal(0, 2, 3);
    }
}

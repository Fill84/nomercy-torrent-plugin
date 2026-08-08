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

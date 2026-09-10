using System;
using System.IO;
using System.Text;
using Xunit;

namespace ComfyTray.Tests;

/// <summary>
/// Tests for the framing layer. It is written against <see cref="Stream"/> rather than a named
/// pipe precisely so these can drive the same code the service runs.
/// </summary>
public sealed class MessageFramingTests
{
    private static FrameReader ReaderOver(string content) =>
        new(new MemoryStream(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public void ReadsFramesInOrder()
    {
        var reader = ReaderOver("first\nsecond\nthird\n");

        Assert.Equal("first", reader.ReadLine());
        Assert.Equal("second", reader.ReadLine());
        Assert.Equal("third", reader.ReadLine());
        Assert.Null(reader.ReadLine());
    }

    [Fact]
    public void EndOfStreamBetweenFramesIsACleanHangUp() =>
        Assert.Null(ReaderOver(string.Empty).ReadLine());

    /// <summary>
    /// A stream that stops part-way through a frame is a peer that died mid-write, not a tidy
    /// disconnection. Reporting it lets the connection handler log the difference.
    /// </summary>
    [Fact]
    public void EndOfStreamMidFrameIsAnError()
    {
        var reader = ReaderOver("complete\ntruncated");

        Assert.Equal("complete", reader.ReadLine());
        Assert.Throws<InvalidDataException>(() => reader.ReadLine());
    }

    [Fact]
    public void ToleratesCarriageReturns()
    {
        var reader = ReaderOver("first\r\nsecond\r\n");

        Assert.Equal("first", reader.ReadLine());
        Assert.Equal("second", reader.ReadLine());
    }

    [Fact]
    public void HandlesEmptyFrames()
    {
        var reader = ReaderOver("\nafter\n");

        Assert.Equal(string.Empty, reader.ReadLine());
        Assert.Equal("after", reader.ReadLine());
    }

    /// <summary>Frames larger than the internal buffer must reassemble across reads.</summary>
    [Fact]
    public void ReassemblesFramesLargerThanTheBuffer()
    {
        var payload = new string('x', 40_000);
        var reader = ReaderOver(payload + "\nnext\n");

        Assert.Equal(payload, reader.ReadLine());
        Assert.Equal("next", reader.ReadLine());
    }

    [Fact]
    public void PreservesNonAsciiPayloads()
    {
        var payload = @"c:\comfyui\modèles\π.exe";
        Assert.Equal(payload, ReaderOver(payload + "\n").ReadLine());
    }

    /// <summary>
    /// The guard reads from a pipe any interactive user can reach. A caller that opens a
    /// connection and never sends a newline must not be able to exhaust the service's memory.
    /// </summary>
    [Fact]
    public void RefusesAFrameOverTheLimit()
    {
        var reader = ReaderOver(new string('x', MessageFraming.MaxLineBytes + 1) + "\n");

        Assert.Throws<InvalidDataException>(() => reader.ReadLine());
    }

    [Fact]
    public void RefusesToWriteAFrameOverTheLimit()
    {
        using var stream = new MemoryStream();

        Assert.Throws<InvalidDataException>(() =>
            MessageFraming.Write(stream, new string('x', MessageFraming.MaxLineBytes)));
    }

    [Fact]
    public void WriteThenReadRoundTrips()
    {
        using var stream = new MemoryStream();
        MessageFraming.Write(stream, "one");
        MessageFraming.Write(stream, "two");
        stream.Position = 0;

        var reader = new FrameReader(stream);
        Assert.Equal("one", reader.ReadLine());
        Assert.Equal("two", reader.ReadLine());
        Assert.Null(reader.ReadLine());
    }

    /// <summary>A serialised message must survive the framing it will actually travel through.</summary>
    [Fact]
    public void CarriesASerialisedMessage()
    {
        using var stream = new MemoryStream();
        MessageFraming.Write(
            stream, MessageFraming.Serialize(new BeginSessionRequest("1.0.0", 99, 60)));
        stream.Position = 0;

        var line = new FrameReader(stream).ReadLine();
        Assert.NotNull(line);

        var request = Assert.IsType<BeginSessionRequest>(MessageFraming.DeserializeRequest(line));
        Assert.Equal(99, request.ComfyPid);
    }
}

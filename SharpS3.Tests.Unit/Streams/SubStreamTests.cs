namespace SharpS3.Tests.Unit;

using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3.Streams;

[TestClass]
public class SubStreamTests
{
    [TestMethod]
    public void SubStream_BasicProperties_And_Seek_WorkAsExpected()
    {
        byte[] data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        using var mem = new MemoryStream(data);
        using var sub = new SubStream(mem, offset: 2, length: 5); // slice: [2, 3, 4, 5, 6]

        sub.CanRead.Should().BeTrue();
        sub.CanSeek.Should().BeTrue();
        sub.CanWrite.Should().BeFalse();
        sub.Length.Should().Be(5);
        sub.Position.Should().Be(0);

        // Seek Begin
        sub.Seek(2, SeekOrigin.Begin).Should().Be(2);
        sub.Position.Should().Be(2);

        // Seek Current
        sub.Seek(1, SeekOrigin.Current).Should().Be(3);
        sub.Position.Should().Be(3);

        // Seek End
        sub.Seek(-1, SeekOrigin.End).Should().Be(4);
        sub.Position.Should().Be(4);

        // Set Position directly
        sub.Position = 1;
        sub.Position.Should().Be(1);

        // Out of range position
        Action actNegative = () => sub.Position = -1;
        actNegative.Should().Throw<ArgumentOutOfRangeException>();

        Action actTooLarge = () => sub.Position = 6;
        actTooLarge.Should().Throw<ArgumentOutOfRangeException>();

        Action actInvalidOrigin = () => sub.Seek(0, (SeekOrigin)999);
        actInvalidOrigin.Should().Throw<ArgumentOutOfRangeException>();

        // Flush
        sub.Flush();

        // Unsupported write operations
        Action actSetLength = () => sub.SetLength(10);
        actSetLength.Should().Throw<NotSupportedException>();

        Action actWrite = () => sub.Write(new byte[1], 0, 1);
        actWrite.Should().Throw<NotSupportedException>();
    }

    [TestMethod]
    public void SubStream_SynchronousRead_ReadsCorrectSlice()
    {
        byte[] data = new byte[] { 10, 20, 30, 40, 50, 60, 70 };
        using var mem = new MemoryStream(data);
        using var sub = new SubStream(mem, offset: 1, length: 3); // slice: [20, 30, 40]

        byte[] buf = new byte[10];
        int read = sub.Read(buf, 0, 2);
        read.Should().Be(2);
        buf[0].Should().Be(20);
        buf[1].Should().Be(30);

        read = sub.Read(buf, 2, 5);
        read.Should().Be(1);
        buf[2].Should().Be(40);

        // Read at end of stream returns 0
        read = sub.Read(buf, 0, 1);
        read.Should().Be(0);
    }

    [TestMethod]
    public async Task SubStream_AsyncRead_ReadsCorrectSlice()
    {
        byte[] data = new byte[] { 100, 101, 102, 103, 104 };
        using var mem = new MemoryStream(data);
        using var sub = new SubStream(mem, offset: 2, length: 2); // slice: [102, 103]

        byte[] buf = new byte[4];
        int read = await sub.ReadAsync(buf, 0, 4);
        read.Should().Be(2);
        buf[0].Should().Be(102);
        buf[1].Should().Be(103);

        // Read past length
        read = await sub.ReadAsync(buf, 0, 4);
        read.Should().Be(0);
    }

    [TestMethod]
    public void SubStream_Constructor_NullBaseStream_ThrowsArgumentNullException()
    {
        Action act = () => new SubStream(null!, 0, 10);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("baseStream");
    }

    [TestMethod]
    public void SubStream_Constructor_NonSeekableStream_ThrowsArgumentException()
    {
        using var nonSeekable = new NonSeekableStream();
        Action act = () => new SubStream(nonSeekable, 0, 10);
        act.Should().Throw<ArgumentException>()
            .WithParameterName("baseStream")
            .WithMessage("*Base stream must be seekable.*");
    }

    [TestMethod]
    public void SubStream_Constructor_NegativeOffset_ThrowsArgumentOutOfRangeException()
    {
        using var mem = new MemoryStream(new byte[10]);
        Action act = () => new SubStream(mem, -1, 5);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("offset");
    }

    [TestMethod]
    public void SubStream_Constructor_NegativeLength_ThrowsArgumentOutOfRangeException()
    {
        using var mem = new MemoryStream(new byte[10]);
        Action act = () => new SubStream(mem, 0, -1);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("length");
    }

    private class NonSeekableStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

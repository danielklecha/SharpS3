#pragma warning disable CA2022

namespace SharpS3.Tests.Unit;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Streams;

[TestClass]
public class BoundedReadStreamTests
{
    private class NonSeekableStream : Stream
    {
        private readonly Stream _Inner;
        public NonSeekableStream(Stream inner) => _Inner = inner;
        public override bool CanRead => _Inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _Inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _Inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => _Inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _Inner.ReadAsync(buffer, offset, count, cancellationToken);
#if NETCOREAPP || NETSTANDARD2_1_OR_GREATER
        public override int Read(Span<byte> buffer) => _Inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _Inner.ReadAsync(buffer, cancellationToken);
#endif
    }

    [TestMethod]
    public void Constructor_Validation()
    {
        Action actNull = () => new BoundedReadStream(null!, 100);
        actNull.Should().Throw<ArgumentNullException>();

        using var ms = new MemoryStream();
        Action actNeg = () => new BoundedReadStream(ms, -1);
        actNeg.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void SeekableStream_ExceedingLimit_Throws_EntityTooLarge_At_Construction()
    {
        byte[] data = new byte[200];
        using var ms = new MemoryStream(data);

        Action act = () => new BoundedReadStream(ms, 100);
        var ex = act.Should().Throw<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);
    }

    [TestMethod]
    public void SeekableStream_WithinLimit_ReadsCorrectly()
    {
        byte[] data = new byte[] { 1, 2, 3, 4, 5 };
        using var ms = new MemoryStream(data);
        using var bounded = new BoundedReadStream(ms, 10);

        bounded.CanRead.Should().BeTrue();
        bounded.CanSeek.Should().BeFalse();
        bounded.CanWrite.Should().BeFalse();
        bounded.Length.Should().Be(5);
        bounded.MaxBytes.Should().Be(10);

        byte[] dest = new byte[10];
        int read = bounded.Read(dest, 0, dest.Length);
        read.Should().Be(5);
        bounded.BytesRead.Should().Be(5);
        bounded.Position.Should().Be(5);

        // Subsequent read returns 0 (EOF)
        bounded.Read(dest, 0, dest.Length).Should().Be(0);
    }

    [TestMethod]
    public async Task NonSeekableStream_WithinLimit_ReadsCorrectly_Async()
    {
        byte[] data = new byte[] { 10, 20, 30 };
        using var ms = new MemoryStream(data);
        using var nonSeek = new NonSeekableStream(ms);
        using var bounded = new BoundedReadStream(nonSeek, 3);

        byte[] dest = new byte[10];
        int read = await bounded.ReadAsync(dest, 0, dest.Length);
        read.Should().Be(3);
        bounded.BytesRead.Should().Be(3);

        int eof = await bounded.ReadAsync(dest, 0, dest.Length);
        eof.Should().Be(0);
    }

    [TestMethod]
    public async Task NonSeekableStream_ExceedingLimit_Throws_EntityTooLarge_Async()
    {
        byte[] data = new byte[] { 1, 2, 3, 4, 5 };
        using var ms = new MemoryStream(data);
        using var nonSeek = new NonSeekableStream(ms);
        using var bounded = new BoundedReadStream(nonSeek, 3);

        byte[] dest = new byte[2];
        // Read 2 bytes (total 2 <= 3)
        int r1 = await bounded.ReadAsync(dest, 0, 2);
        r1.Should().Be(2);

        // Try reading next chunk; total would exceed 3
        var act = async () =>
        {
            byte[] dest2 = new byte[10];
            _ = await bounded.ReadAsync(dest2, 0, dest2.Length);
        };

        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);
    }

    [TestMethod]
    public void NonSeekableStream_ExceedingLimit_Throws_EntityTooLarge_Sync()
    {
        byte[] data = new byte[] { 1, 2, 3, 4, 5 };
        using var ms = new MemoryStream(data);
        using var nonSeek = new NonSeekableStream(ms);
        using var bounded = new BoundedReadStream(nonSeek, 2);

        byte[] dest = new byte[10];
        Action act = () => { _ = bounded.Read(dest, 0, dest.Length); };
        var ex = act.Should().Throw<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);
    }

    [TestMethod]
    public void Unsupported_Operations_Throw()
    {
        using var ms = new MemoryStream();
        using var bounded = new BoundedReadStream(ms, 10);

        Action actSeek = () => bounded.Seek(0, SeekOrigin.Begin);
        actSeek.Should().Throw<NotSupportedException>();

        Action actSetLen = () => bounded.SetLength(5);
        actSetLen.Should().Throw<NotSupportedException>();

        Action actWrite = () => bounded.Write(new byte[1], 0, 1);
        actWrite.Should().Throw<NotSupportedException>();

        Action actSetPos = () => bounded.Position = 0;
        actSetPos.Should().Throw<NotSupportedException>();
    }

    [TestMethod]
    public void LeaveOpen_Controls_Disposal()
    {
        var ms1 = new MemoryStream();
        using (var b1 = new BoundedReadStream(ms1, 10, leaveOpen: false)) { }
        Action act1 = () => ms1.Read(new byte[1], 0, 1);
        act1.Should().Throw<ObjectDisposedException>();

        var ms2 = new MemoryStream();
        using (var b2 = new BoundedReadStream(ms2, 10, leaveOpen: true)) { }
        ms2.CanRead.Should().BeTrue();
        ms2.Dispose();
    }

    private class TestBoundedReadStream : BoundedReadStream
    {
        public TestBoundedReadStream(Stream inner, long maxBytes, bool leaveOpen = false)
            : base(inner, maxBytes, leaveOpen) { }
        public void ExposeDispose(bool disposing) => Dispose(disposing);
    }

    [TestMethod]
    public void Dispose_False_DoesNotDisposeInnerStream()
    {
        var ms = new MemoryStream();
        var b = new TestBoundedReadStream(ms, 10, leaveOpen: false);
        b.ExposeDispose(false);
        ms.CanRead.Should().BeTrue();
        b.Dispose();
        ms.CanRead.Should().BeFalse();
    }

    [TestMethod]
    public void NonSeekableStream_Length_Throws_NotSupportedException()
    {
        using var ms = new MemoryStream(new byte[10]);
        using var nonSeek = new NonSeekableStream(ms);
        using var bounded = new BoundedReadStream(nonSeek, 10);

        Action act = () => _ = bounded.Length;
        act.Should().Throw<NotSupportedException>();
    }

    [TestMethod]
    public async Task Flush_And_FlushAsync_DelegateToInnerStream()
    {
        using var ms = new MemoryStream();
        using var bounded = new BoundedReadStream(ms, 10);

        Action act = () => bounded.Flush();
        act.Should().NotThrow();

        Func<Task> actAsync = async () => await bounded.FlushAsync();
        await actAsync.Should().NotThrowAsync();
    }

    [TestMethod]
    public void Read_WithZeroOrNegativeCount_ReturnsZero()
    {
        byte[] data = new byte[] { 1, 2, 3 };
        using var ms = new MemoryStream(data);
        using var bounded = new BoundedReadStream(ms, 10);

        byte[] dest = new byte[5];
        bounded.Read(dest, 0, 0).Should().Be(0);
        bounded.BytesRead.Should().Be(0);

        bounded.Read(dest, 0, -1).Should().Be(0);
        bounded.BytesRead.Should().Be(0);
    }

    [TestMethod]
    public void Read_WhenBytesReadReachesMax_ThrowsIfMoreDataAvailable()
    {
        byte[] data = new byte[] { 1, 2, 3, 4, 5 };
        using var ms = new MemoryStream(data);
        using var nonSeek = new NonSeekableStream(ms);
        using var bounded = new BoundedReadStream(nonSeek, 3);

        byte[] dest = new byte[3];
        int read = bounded.Read(dest, 0, 3);
        read.Should().Be(3);
        bounded.BytesRead.Should().Be(3);

        // _BytesRead >= _MaxBytes, but inner stream has more bytes -> throws EntityTooLarge
        Action act = () => bounded.Read(dest, 0, 3);
        var ex = act.Should().Throw<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);
        bounded.BytesRead.Should().Be(4);
    }

    [TestMethod]
    public void Read_WhenBytesReadReachesMax_ReturnsZeroIfAtEof()
    {
        byte[] data = new byte[] { 1, 2, 3 };
        using var ms = new MemoryStream(data);
        using var nonSeek = new NonSeekableStream(ms);
        using var bounded = new BoundedReadStream(nonSeek, 3);

        byte[] dest = new byte[3];
        int read = bounded.Read(dest, 0, 3);
        read.Should().Be(3);
        bounded.BytesRead.Should().Be(3);

        // _BytesRead >= _MaxBytes, and inner stream is at EOF -> returns 0
        bounded.Read(dest, 0, 3).Should().Be(0);
        bounded.BytesRead.Should().Be(3);
    }

    [TestMethod]
    public async Task ReadAsync_WithZeroOrNegativeCount_ReturnsZero()
    {
        byte[] data = new byte[] { 1, 2, 3 };
        using var ms = new MemoryStream(data);
        using var bounded = new BoundedReadStream(ms, 10);

        byte[] dest = new byte[5];
        int r1 = await bounded.ReadAsync(dest, 0, 0);
        r1.Should().Be(0);
        bounded.BytesRead.Should().Be(0);

        int r2 = await bounded.ReadAsync(dest, 0, -1);
        r2.Should().Be(0);
        bounded.BytesRead.Should().Be(0);
    }

    [TestMethod]
    public async Task ReadAsync_WhenBytesReadReachesMax_ThrowsIfMoreDataAvailable()
    {
        byte[] data = new byte[] { 1, 2, 3, 4, 5 };
        using var ms = new MemoryStream(data);
        using var nonSeek = new NonSeekableStream(ms);
        using var bounded = new BoundedReadStream(nonSeek, 3);

        byte[] dest = new byte[3];
        int read = await bounded.ReadAsync(dest, 0, 3);
        read.Should().Be(3);
        bounded.BytesRead.Should().Be(3);

        Func<Task> act = async () => await bounded.ReadAsync(dest, 0, 3);
        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);
        bounded.BytesRead.Should().Be(4);
    }

    [TestMethod]
    public async Task ReadAsync_WhenInnerStreamReachesEofBeforeMax_ReturnsZero()
    {
        byte[] data = new byte[] { 1, 2 };
        using var ms = new MemoryStream(data);
        using var nonSeek = new NonSeekableStream(ms);
        using var bounded = new BoundedReadStream(nonSeek, 10);

        byte[] dest = new byte[10];
        int read = await bounded.ReadAsync(dest, 0, 10);
        read.Should().Be(2);
        bounded.BytesRead.Should().Be(2);

        // _BytesRead < _MaxBytes, but inner stream is exhausted -> returns 0
        int eof = await bounded.ReadAsync(dest, 0, 10);
        eof.Should().Be(0);
        bounded.BytesRead.Should().Be(2);
    }

#if NETCOREAPP || NETSTANDARD2_1_OR_GREATER
    [TestMethod]
    public async Task ReadAsync_Memory_ZeroLength_ReturnsZero()
    {
        byte[] data = new byte[] { 1, 2, 3 };
        using var ms = new MemoryStream(data);
        using var bounded = new BoundedReadStream(ms, 10);

        int read = await bounded.ReadAsync(Memory<byte>.Empty);
        read.Should().Be(0);
        bounded.BytesRead.Should().Be(0);
    }

    [TestMethod]
    public async Task ReadAsync_Memory_WithinLimit_ReadsCorrectly()
    {
        byte[] data = new byte[] { 1, 2, 3, 4, 5 };
        using var ms = new MemoryStream(data);
        using var bounded = new BoundedReadStream(ms, 10);

        byte[] dest = new byte[10];
        int read = await bounded.ReadAsync(dest.AsMemory(0, 5));
        read.Should().Be(5);
        bounded.BytesRead.Should().Be(5);

        // Next read reaches EOF
        int eof = await bounded.ReadAsync(dest.AsMemory());
        eof.Should().Be(0);
        bounded.BytesRead.Should().Be(5);
    }

    [TestMethod]
    public async Task ReadAsync_Memory_WhenInnerStreamReachesEofBeforeMax_ReturnsZero()
    {
        byte[] data = new byte[] { 1, 2 };
        using var ms = new MemoryStream(data);
        using var nonSeek = new NonSeekableStream(ms);
        using var bounded = new BoundedReadStream(nonSeek, 10);

        byte[] dest = new byte[10];
        int read = await bounded.ReadAsync(dest.AsMemory());
        read.Should().Be(2);
        bounded.BytesRead.Should().Be(2);

        int eof = await bounded.ReadAsync(dest.AsMemory());
        eof.Should().Be(0);
        bounded.BytesRead.Should().Be(2);
    }

    [TestMethod]
    public async Task ReadAsync_Memory_ExceedingLimit_Throws_EntityTooLarge()
    {
        byte[] data = new byte[] { 1, 2, 3, 4, 5 };
        using var ms = new MemoryStream(data);
        using var nonSeek = new NonSeekableStream(ms);
        using var bounded = new BoundedReadStream(nonSeek, 2);

        byte[] dest = new byte[10];
        Func<Task> act = async () => await bounded.ReadAsync(dest.AsMemory());
        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);
    }

    [TestMethod]
    public async Task ReadAsync_Memory_WhenBytesReadReachesMax_ThrowsIfMoreDataAvailable()
    {
        byte[] data = new byte[] { 1, 2, 3, 4, 5 };
        using var ms = new MemoryStream(data);
        using var nonSeek = new NonSeekableStream(ms);
        using var bounded = new BoundedReadStream(nonSeek, 3);

        byte[] dest = new byte[3];
        int read = await bounded.ReadAsync(dest.AsMemory(0, 3));
        read.Should().Be(3);
        bounded.BytesRead.Should().Be(3);

        Func<Task> act = async () => await bounded.ReadAsync(dest.AsMemory(0, 3));
        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);
        bounded.BytesRead.Should().Be(4);
    }

    [TestMethod]
    public async Task ReadAsync_Memory_WhenBytesReadReachesMax_ReturnsZeroIfAtEof()
    {
        byte[] data = new byte[] { 1, 2, 3 };
        using var ms = new MemoryStream(data);
        using var nonSeek = new NonSeekableStream(ms);
        using var bounded = new BoundedReadStream(nonSeek, 3);

        byte[] dest = new byte[3];
        int read = await bounded.ReadAsync(dest.AsMemory(0, 3));
        read.Should().Be(3);
        bounded.BytesRead.Should().Be(3);

        int eof = await bounded.ReadAsync(dest.AsMemory(0, 3));
        eof.Should().Be(0);
        bounded.BytesRead.Should().Be(3);
    }

    [TestMethod]
    public void Read_Span_ZeroLength_ReturnsZero()
    {
        byte[] data = new byte[] { 1, 2, 3 };
        using var ms = new MemoryStream(data);
        using var bounded = new BoundedReadStream(ms, 10);

        int read = bounded.Read(Span<byte>.Empty);
        read.Should().Be(0);
        bounded.BytesRead.Should().Be(0);
    }

    [TestMethod]
    public void Read_Span_WithinLimit_ReadsCorrectly()
    {
        byte[] data = new byte[] { 1, 2, 3, 4, 5 };
        using var ms = new MemoryStream(data);
        using var bounded = new BoundedReadStream(ms, 10);

        Span<byte> dest = stackalloc byte[10];
        int read = bounded.Read(dest.Slice(0, 5));
        read.Should().Be(5);
        bounded.BytesRead.Should().Be(5);

        int eof = bounded.Read(dest);
        eof.Should().Be(0);
        bounded.BytesRead.Should().Be(5);
    }

    [TestMethod]
    public void Read_Span_WhenInnerStreamReachesEofBeforeMax_ReturnsZero()
    {
        byte[] data = new byte[] { 1, 2 };
        using var ms = new MemoryStream(data);
        using var nonSeek = new NonSeekableStream(ms);
        using var bounded = new BoundedReadStream(nonSeek, 10);

        Span<byte> dest = stackalloc byte[10];
        int read = bounded.Read(dest);
        read.Should().Be(2);
        bounded.BytesRead.Should().Be(2);

        int eof = bounded.Read(dest);
        eof.Should().Be(0);
        bounded.BytesRead.Should().Be(2);
    }

    [TestMethod]
    public void Read_Span_ExceedingLimit_Throws_EntityTooLarge()
    {
        byte[] data = new byte[] { 1, 2, 3, 4, 5 };
        using var ms = new MemoryStream(data);
        using var nonSeek = new NonSeekableStream(ms);
        using var bounded = new BoundedReadStream(nonSeek, 2);

        Action act = () =>
        {
            Span<byte> dest = stackalloc byte[10];
            bounded.Read(dest);
        };
        var ex = act.Should().Throw<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);
    }

    [TestMethod]
    public void Read_Span_WhenBytesReadReachesMax_ThrowsIfMoreDataAvailable()
    {
        byte[] data = new byte[] { 1, 2, 3, 4, 5 };
        using var ms = new MemoryStream(data);
        using var nonSeek = new NonSeekableStream(ms);
        using var bounded = new BoundedReadStream(nonSeek, 3);

        Span<byte> dest = stackalloc byte[3];
        int read = bounded.Read(dest);
        read.Should().Be(3);
        bounded.BytesRead.Should().Be(3);

        Action act = () =>
        {
            Span<byte> extra = stackalloc byte[3];
            bounded.Read(extra);
        };
        var ex = act.Should().Throw<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);
        bounded.BytesRead.Should().Be(4);
    }

    [TestMethod]
    public void Read_Span_WhenBytesReadReachesMax_ReturnsZeroIfAtEof()
    {
        byte[] data = new byte[] { 1, 2, 3 };
        using var ms = new MemoryStream(data);
        using var nonSeek = new NonSeekableStream(ms);
        using var bounded = new BoundedReadStream(nonSeek, 3);

        Span<byte> dest = stackalloc byte[3];
        int read = bounded.Read(dest);
        read.Should().Be(3);
        bounded.BytesRead.Should().Be(3);

        int eof = bounded.Read(dest);
        eof.Should().Be(0);
        bounded.BytesRead.Should().Be(3);
    }

    [TestMethod]
    public async Task DisposeAsync_Controls_Disposal()
    {
        var ms1 = new MemoryStream();
        var b1 = new BoundedReadStream(ms1, 10, leaveOpen: false);
        await b1.DisposeAsync();
        Action act1 = () => ms1.Read(new byte[1], 0, 1);
        act1.Should().Throw<ObjectDisposedException>();

        var ms2 = new MemoryStream();
        var b2 = new BoundedReadStream(ms2, 10, leaveOpen: true);
        await b2.DisposeAsync();
        ms2.CanRead.Should().BeTrue();
        await ms2.DisposeAsync();
    }
#endif

    [TestMethod]
    public void LargeMaxBytes_DoesNotOverflow()
    {
        byte[] data = new byte[] { 10, 20, 30 };
        using var ms = new MemoryStream(data);
        using var bounded = new BoundedReadStream(ms, long.MaxValue);

        byte[] dest = new byte[10];
        int read = bounded.Read(dest, 0, dest.Length);
        read.Should().Be(3);
        bounded.BytesRead.Should().Be(3);
    }

    [TestMethod]
    public void MaxBytesZero_AllowsEmptyStream_And_RejectsNonEmpty()
    {
        using var emptyMs = new MemoryStream(Array.Empty<byte>());
        using var emptyNonSeek = new NonSeekableStream(emptyMs);
        using var boundedEmpty = new BoundedReadStream(emptyNonSeek, 0);

        byte[] dest = new byte[5];
        boundedEmpty.Read(dest, 0, dest.Length).Should().Be(0);
        boundedEmpty.BytesRead.Should().Be(0);

        using var nonEmptyMs = new MemoryStream(new byte[] { 42 });
        using var nonEmptyNonSeek = new NonSeekableStream(nonEmptyMs);
        using var boundedNonEmpty = new BoundedReadStream(nonEmptyNonSeek, 0);

        Action act = () => boundedNonEmpty.Read(dest, 0, dest.Length);
        var ex = act.Should().Throw<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);
    }
}

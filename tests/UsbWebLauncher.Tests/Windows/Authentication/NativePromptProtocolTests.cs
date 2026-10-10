using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Windows.Authentication.NativePrompt;

namespace UsbWebLauncher.Tests.Windows.Authentication;

public sealed class NativePromptProtocolTests
{
    [Fact]
    public void AttemptIdentity_CreateUsesANewUnpredictableFullNonceForTheSameCoreAttempt()
    {
        var attempt = new AuthenticationAttempt(1, 2, 3);

        var identities = Enumerable.Range(0, 64)
            .Select(_ => NativePromptAttemptId.Create(attempt))
            .ToArray();

        Assert.Equal(identities.Length, identities.Select(value => (value.NonceHigh, value.NonceLow)).Distinct().Count());
        Assert.All(identities, value => Assert.False(value.NonceHigh == 0 && value.NonceLow == 0));
    }

    private const uint Magic = 0x504E5755;
    private const byte Version = 1;
    private const byte ArmRequestType = 1;
    private const byte ArmResponseType = 2;
    private const byte CompletionType = 3;

    [Fact]
    public async Task ArmRequest_RoundTripsOnlyPolicyAttemptAndBoundedMutablePin()
    {
        var pin = CreateRandomSecret(24);
        var attempt = new NativePromptAttemptId(17, 29, 41);
        var stream = new ClearableMemoryStream();
        NativePromptArmRequest? request = null;
        char[]? requestPinBuffer = null;

        try
        {
            await NativePromptProtocol.WriteArmRequestAsync(
                stream,
                "policy-id",
                "ABCDEF012345",
                attempt,
                pin,
                CancellationToken.None);
            stream.Position = 0;

            request = await NativePromptProtocol.ReadArmRequestAsync(stream, CancellationToken.None);

            Assert.Equal("policy-id", request.PolicyId);
            Assert.Equal("ABCDEF012345", request.PolicyHash);
            Assert.Equal(attempt, request.AttemptId);
            Assert.True(pin.AsSpan().SequenceEqual(request.Pin.Span));
            Assert.Equal("Native prompt arm request [REDACTED]", request.ToString());

            Assert.True(MemoryMarshal.TryGetArray(request.Pin, out var pinSegment));
            requestPinBuffer = pinSegment.Array;
            request.Dispose();
            Assert.All(pinSegment.Array!, value => Assert.Equal('\0', value));
        }
        finally
        {
            request?.Dispose();
            if (requestPinBuffer is not null)
            {
                Assert.All(requestPinBuffer, value => Assert.Equal('\0', value));
            }

            stream.Dispose();
            Assert.True(stream.WasCleared);
            Array.Clear(pin, 0, pin.Length);
        }
    }

    [Fact]
    public async Task WriteArmRequest_ClearsByteTransportFrameAfterWrite()
    {
        var pin = CreateRandomSecret(24);
        var stream = new CapturingWriteStream();

        try
        {
            await NativePromptProtocol.WriteArmRequestAsync(
                stream,
                "policy-id",
                "ABCDEF012345",
                new(1, 2, 3),
                pin,
                CancellationToken.None);

            Assert.NotNull(stream.CapturedBuffer);
            Assert.All(stream.CapturedBuffer!, value => Assert.Equal(0, value));
        }
        finally
        {
            Array.Clear(pin, 0, pin.Length);
        }
    }

    [Fact]
    public async Task WriteArmRequest_ClearsByteTransportFrameWhenWriteThrows()
    {
        var pin = CreateRandomSecret(24);
        var stream = new CapturingWriteStream { Failure = new IOException() };

        try
        {
            await Assert.ThrowsAsync<IOException>(async () =>
                await NativePromptProtocol.WriteArmRequestAsync(
                    stream,
                    "policy-id",
                    "ABCDEF012345",
                    new(1, 2, 3),
                    pin,
                    CancellationToken.None));

            Assert.NotNull(stream.CapturedBuffer);
            Assert.All(stream.CapturedBuffer!, value => Assert.Equal(0, value));
        }
        finally
        {
            Array.Clear(pin, 0, pin.Length);
        }
    }

    [Fact]
    public async Task WriteArmRequest_ClearsByteTransportFrameWhenWriteIsCancelled()
    {
        var pin = CreateRandomSecret(24);
        using var cancellation = new CancellationTokenSource();
        var stream = new CapturingWriteStream { CancelOnWrite = cancellation };

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await NativePromptProtocol.WriteArmRequestAsync(
                    stream,
                    "policy-id",
                    "ABCDEF012345",
                    new(1, 2, 3),
                    pin,
                    cancellation.Token));

            Assert.NotNull(stream.CapturedBuffer);
            Assert.All(stream.CapturedBuffer!, value => Assert.Equal(0, value));
        }
        finally
        {
            Array.Clear(pin, 0, pin.Length);
        }
    }

    [Fact]
    public async Task WriteArmRequest_RejectsPinOverCharacterLimitBeforeWriting()
    {
        var pin = CreateRandomSecret(NativePromptProtocol.MaxPinCharacters + 1);
        var stream = new CapturingWriteStream();

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await NativePromptProtocol.WriteArmRequestAsync(
                    stream,
                    "policy-id",
                    "ABCDEF012345",
                    new(1, 2, null),
                    pin,
                    CancellationToken.None));
            Assert.Null(stream.CapturedBuffer);
            Assert.Equal(0, stream.Length);
        }
        finally
        {
            Array.Clear(pin, 0, pin.Length);
        }
    }

    [Fact]
    public async Task WriteArmRequest_InvalidPinEncodingDoesNotRetainEncoderException()
    {
        var pin = CreateRandomSecret(24);
        pin[0] = '\uD800';

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await NativePromptProtocol.WriteArmRequestAsync(
                    Stream.Null,
                    "policy-id",
                    "ABCDEF012345",
                    new(1, 2, 3),
                    pin,
                    CancellationToken.None));

            Assert.Null(exception.InnerException);
        }
        finally
        {
            Array.Clear(pin, 0, pin.Length);
        }
    }

    [Theory]
    [InlineData(0x10203040u, Version, ArmResponseType, 0)]
    [InlineData(Magic, 2, ArmResponseType, 0)]
    [InlineData(Magic, Version, 99, 0)]
    [InlineData(Magic, Version, ArmResponseType, -1)]
    [InlineData(Magic, Version, ArmResponseType, NativePromptProtocol.MaxPayloadBytes + 1)]
    public async Task ReadFrame_RejectsWrongMagicVersionMessageTypeAndInvalidLengths(
        uint magic,
        byte version,
        byte messageType,
        int payloadLength)
    {
        var frame = Header(magic, version, messageType, payloadLength);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await NativePromptProtocol.ReadArmResponseAsync(
                new MemoryStream(frame),
                CancellationToken.None));
    }

    [Fact]
    public async Task ReadFrame_RejectsFrameLargerThanSixteenKiB()
    {
        var frame = Header(Magic, Version, ArmResponseType, NativePromptProtocol.MaxPayloadBytes + 1);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await NativePromptProtocol.ReadArmResponseAsync(
                new MemoryStream(frame),
                CancellationToken.None));
    }

    [Fact]
    public async Task ReadFrame_RejectsNonZeroReservedHeader()
    {
        var frame = Header(Magic, Version, ArmResponseType, 0, reserved: 1);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await NativePromptProtocol.ReadArmResponseAsync(
                new MemoryStream(frame),
                CancellationToken.None));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(NativePromptProtocol.HeaderBytes - 1)]
    public async Task ReadFrame_RejectsTruncatedHeader(int availableBytes)
    {
        var header = Header(Magic, Version, ArmResponseType, 0);

        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await NativePromptProtocol.ReadArmResponseAsync(
                new MemoryStream(header, 0, availableBytes, writable: false),
                CancellationToken.None));
    }

    [Fact]
    public async Task ReadFrame_RejectsTruncatedPayloadAndClearsReadBuffers()
    {
        var frame = Header(Magic, Version, ArmResponseType, 20)
            .Concat(new byte[7])
            .ToArray();
        var stream = new CapturingReadStream(frame);

        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await NativePromptProtocol.ReadArmResponseAsync(stream, CancellationToken.None));

        Assert.NotEmpty(stream.CapturedBuffers);
        Assert.All(stream.CapturedBuffers.SelectMany(value => value), value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ReadArmResponse_RejectsUnexpectedCompletionMessage()
    {
        var payload = ResponsePayload(new(1, 2, 3), status: 0, publicErrorCode: null);
        var frame = Frame(CompletionType, payload);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await NativePromptProtocol.ReadArmResponseAsync(
                new MemoryStream(frame),
                CancellationToken.None));
    }

    [Fact]
    public async Task ReadArmResponse_RejectsUnknownStatus()
    {
        var payload = ResponsePayload(new(1, 2, 3), status: 99, publicErrorCode: null);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await NativePromptProtocol.ReadArmResponseAsync(
                new MemoryStream(Frame(ArmResponseType, payload)),
                CancellationToken.None));
    }

    [Fact]
    public async Task ReadArmResponse_RejectsMalformedErrorMarker()
    {
        var payload = ResponsePayload(new(1, 2, 3), status: 0, publicErrorCode: null);
        payload[^1] = 2;

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await NativePromptProtocol.ReadArmResponseAsync(
                new MemoryStream(Frame(ArmResponseType, payload)),
                CancellationToken.None));
    }

    [Fact]
    public async Task ReadCompletion_RejectsUnknownStatus()
    {
        var payload = ResponsePayload(new(1, 2, null), status: 99, publicErrorCode: null);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await NativePromptProtocol.ReadCompletionAsync(
                new MemoryStream(Frame(CompletionType, payload)),
                CancellationToken.None));
    }

    [Fact]
    public async Task ReadResponse_RejectsTrailingPayloadData()
    {
        var payload = ResponsePayload(new(1, 2, 3), status: 0, publicErrorCode: null)
            .Concat(new byte[] { 0xCC })
            .ToArray();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await NativePromptProtocol.ReadArmResponseAsync(
                new MemoryStream(Frame(ArmResponseType, payload)),
                CancellationToken.None));
    }

    [Fact]
    public async Task ReadArmRequest_RejectsMalformedTrailingDataAndClearsPayloadBuffers()
    {
        var pin = CreateRandomSecret(24);
        var basePayload = ArmPayload("policy-id", "ABCDEF012345", new(1, 2, 3), pin);
        var payload = basePayload.Concat(new byte[] { 0xCC }).ToArray();
        var frame = Frame(ArmRequestType, payload);
        var stream = new CapturingReadStream(frame);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await NativePromptProtocol.ReadArmRequestAsync(stream, CancellationToken.None));

            Assert.All(stream.CapturedBuffers.SelectMany(value => value), value => Assert.Equal(0, value));
        }
        finally
        {
            Array.Clear(pin, 0, pin.Length);
            Array.Clear(basePayload, 0, basePayload.Length);
            Array.Clear(payload, 0, payload.Length);
            Array.Clear(frame, 0, frame.Length);
        }
    }

    [Fact]
    public async Task ReadArmRequest_ParsesSuccessfullyAndClearsTransportAndOwnedPinBuffers()
    {
        var pin = CreateRandomSecret(24);
        var payload = ArmPayload("policy-id", "ABCDEF012345", new(1, 2, 3), pin);
        var frame = Frame(ArmRequestType, payload);
        var stream = new CapturingReadStream(frame);
        NativePromptArmRequest? request = null;
        char[]? requestPinBuffer = null;

        try
        {
            request = await NativePromptProtocol.ReadArmRequestAsync(stream, CancellationToken.None);

            Assert.True(pin.AsSpan().SequenceEqual(request.Pin.Span));
            Assert.All(stream.CapturedBuffers.SelectMany(value => value), value => Assert.Equal(0, value));
            Assert.True(MemoryMarshal.TryGetArray(request.Pin, out var pinSegment));
            requestPinBuffer = pinSegment.Array;
        }
        finally
        {
            request?.Dispose();
            if (requestPinBuffer is not null)
            {
                Assert.All(requestPinBuffer, value => Assert.Equal('\0', value));
            }

            Array.Clear(pin, 0, pin.Length);
            Array.Clear(payload, 0, payload.Length);
            Array.Clear(frame, 0, frame.Length);
        }
    }

    [Fact]
    public async Task ArmAndCompletionResponses_RoundTripExplicitAttemptStatusAndPublicCode()
    {
        var attempt = new NativePromptAttemptId(7, 11, 13);
        var armStream = new MemoryStream();
        await NativePromptProtocol.WriteArmResponseAsync(
            armStream,
            new(attempt, ClientCertificateNativePromptArmStatus.Replayed, 21),
            CancellationToken.None);
        armStream.Position = 0;

        var arm = await NativePromptProtocol.ReadArmResponseAsync(armStream, CancellationToken.None);

        Assert.Equal(attempt, arm.AttemptId);
        Assert.Equal(ClientCertificateNativePromptArmStatus.Replayed, arm.Status);
        Assert.Equal(21, arm.PublicErrorCode);

        var completionStream = new MemoryStream();
        await NativePromptProtocol.WriteCompletionAsync(
            completionStream,
            new(attempt, ClientCertificateNativePromptStatus.FocusLost, 34),
            CancellationToken.None);
        completionStream.Position = 0;

        var completion = await NativePromptProtocol.ReadCompletionAsync(completionStream, CancellationToken.None);

        Assert.Equal(attempt, completion.AttemptId);
        Assert.Equal(ClientCertificateNativePromptStatus.FocusLost, completion.Status);
        Assert.Equal(34, completion.PublicErrorCode);
    }

    [Fact]
    public async Task ReadArmRequest_RejectsDeclaredPinOverCharacterLimit()
    {
        var pin = CreateRandomSecret(NativePromptProtocol.MaxPinCharacters + 1);
        var payload = ArmPayload("policy-id", "ABCDEF012345", new(1, 2, null), pin);
        var frame = Frame(ArmRequestType, payload);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await NativePromptProtocol.ReadArmRequestAsync(
                    new MemoryStream(frame),
                    CancellationToken.None));
        }
        finally
        {
            Array.Clear(pin, 0, pin.Length);
            Array.Clear(payload, 0, payload.Length);
            Array.Clear(frame, 0, frame.Length);
        }
    }

    [Fact]
    public async Task ReadArmRequest_MalformedPinEncodingDoesNotRetainDecoderException()
    {
        var pin = CreateRandomSecret(24);
        pin[0] = '\uD800';
        var payload = ArmPayload("policy-id", "ABCDEF012345", new(1, 2, 3), pin);
        var pinOffset = payload.Length - checked(pin.Length * sizeof(char));
        payload[pinOffset] = 0x00;
        payload[pinOffset + 1] = 0xD8;
        var frame = Frame(ArmRequestType, payload);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await NativePromptProtocol.ReadArmRequestAsync(
                    new MemoryStream(frame),
                    CancellationToken.None));

            Assert.Null(exception.InnerException);
        }
        finally
        {
            Array.Clear(pin, 0, pin.Length);
            Array.Clear(payload, 0, payload.Length);
            Array.Clear(frame, 0, frame.Length);
        }
    }

    private static byte[] Header(
        uint magic,
        byte version,
        byte messageType,
        int payloadLength,
        ushort reserved = 0)
    {
        var header = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, magic);
        header[4] = version;
        header[5] = messageType;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), reserved);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), payloadLength);
        return header;
    }

    private static byte[] Frame(byte messageType, byte[] payload) =>
        Header(Magic, Version, messageType, payload.Length).Concat(payload).ToArray();

    private static byte[] ArmPayload(
        string policyId,
        string policyHash,
        NativePromptAttemptId attempt,
        char[] pin)
    {
        var policyBytes = Encoding.UTF8.GetBytes(policyId);
        var hashBytes = Encoding.UTF8.GetBytes(policyHash);
        var pinBytes = Encoding.Unicode.GetBytes(pin);
        var browserBytes = attempt.BrowserOperationId.HasValue ? sizeof(long) : 0;
        var payload = new byte[
            sizeof(ushort) + policyBytes.Length +
            sizeof(ushort) + hashBytes.Length +
            sizeof(long) + sizeof(long) + sizeof(byte) + browserBytes +
            sizeof(ushort) + pinBytes.Length];
        var offset = 0;
        WriteLengthPrefixed(policyBytes, payload, ref offset);
        WriteLengthPrefixed(hashBytes, payload, ref offset);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset), attempt.NonceHigh);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset), attempt.NonceLow);
        offset += sizeof(long);
        payload[offset++] = attempt.BrowserOperationId.HasValue ? (byte)1 : (byte)0;
        if (attempt.BrowserOperationId is long browserOperationId)
        {
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset), browserOperationId);
            offset += sizeof(long);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), checked((ushort)pin.Length));
        offset += sizeof(ushort);
        pinBytes.CopyTo(payload, offset);
        Array.Clear(policyBytes, 0, policyBytes.Length);
        Array.Clear(hashBytes, 0, hashBytes.Length);
        Array.Clear(pinBytes, 0, pinBytes.Length);
        return payload;
    }

    private static byte[] ResponsePayload(
        NativePromptAttemptId attempt,
        int status,
        int? publicErrorCode)
    {
        var browserBytes = attempt.BrowserOperationId.HasValue ? sizeof(long) : 0;
        var errorBytes = publicErrorCode.HasValue ? sizeof(int) : 0;
        var payload = new byte[
            sizeof(long) + sizeof(long) + sizeof(byte) + browserBytes +
            sizeof(int) + sizeof(byte) + errorBytes];
        var offset = 0;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset), attempt.NonceHigh);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset), attempt.NonceLow);
        offset += sizeof(long);
        payload[offset++] = attempt.BrowserOperationId.HasValue ? (byte)1 : (byte)0;
        if (attempt.BrowserOperationId is long browserOperationId)
        {
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset), browserOperationId);
            offset += sizeof(long);
        }

        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset), status);
        offset += sizeof(int);
        payload[offset++] = publicErrorCode.HasValue ? (byte)1 : (byte)0;
        if (publicErrorCode is int errorCode)
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset), errorCode);
        }

        return payload;
    }

    private static void WriteLengthPrefixed(byte[] value, byte[] destination, ref int offset)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset), checked((ushort)value.Length));
        offset += sizeof(ushort);
        value.CopyTo(destination, offset);
        offset += value.Length;
    }

    private static char[] CreateRandomSecret(int length)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        try
        {
            return bytes.Select(value => (char)('!' + value % 90)).ToArray();
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
        }
    }

    private sealed class CapturingWriteStream : Stream
    {
        private long _length;

        public byte[]? CapturedBuffer { get; private set; }
        public Exception? Failure { get; init; }
        public CancellationTokenSource? CancelOnWrite { get; init; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => Length; set => throw new NotSupportedException(); }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Assert.True(MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)buffer, out var segment));
            CapturedBuffer = segment.Array;
            CancelOnWrite?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                throw Failure;
            }

            _length += buffer.Length;
            return ValueTask.CompletedTask;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ClearableMemoryStream : MemoryStream
    {
        public bool WasCleared { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing && TryGetBuffer(out var segment))
            {
                Array.Clear(segment.Array!, 0, segment.Array!.Length);
                WasCleared = segment.Array.All(value => value == 0);
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CapturingReadStream(byte[] source) : Stream
    {
        private int _offset;

        public List<byte[]> CapturedBuffers { get; } = [];

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)buffer, out var segment));
            CapturedBuffers.Add(segment.Array!);
            var count = Math.Min(buffer.Length, source.Length - _offset);
            source.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

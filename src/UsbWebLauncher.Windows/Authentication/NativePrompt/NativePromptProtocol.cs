using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.Windows.Authentication.NativePrompt;

internal readonly record struct NativePromptAttemptId(
    long NonceHigh,
    long NonceLow,
    long? BrowserOperationId)
{
    public static NativePromptAttemptId Create(AuthenticationAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        Span<byte> nonce = stackalloc byte[sizeof(long) * 2];
        long high;
        long low;
        do
        {
            RandomNumberGenerator.Fill(nonce);
            high = BinaryPrimitives.ReadInt64LittleEndian(nonce);
            low = BinaryPrimitives.ReadInt64LittleEndian(nonce[sizeof(long)..]);
        }
        while (high == 0 && low == 0);

        nonce.Clear();
        return new(high, low, attempt.BrowserOperationId);
    }
}

internal sealed class NativePromptArmRequest : IDisposable
{
    private readonly char[] _pin;
    private int _disposed;

    public NativePromptArmRequest(
        string policyId,
        string policyHash,
        NativePromptAttemptId attemptId,
        char[] pin)
    {
        PolicyId = policyId;
        PolicyHash = policyHash;
        AttemptId = attemptId;
        _pin = pin;
    }

    public string PolicyId { get; }

    public string PolicyHash { get; }

    public NativePromptAttemptId AttemptId { get; }

    public ReadOnlyMemory<char> Pin => _pin;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Array.Clear(_pin, 0, _pin.Length);
        }
    }

    public override string ToString() => "Native prompt arm request [REDACTED]";
}

internal readonly record struct NativePromptArmResponse(
    NativePromptAttemptId AttemptId,
    ClientCertificateNativePromptArmStatus Status,
    int? PublicErrorCode = null);

internal readonly record struct NativePromptCompletionResponse(
    NativePromptAttemptId AttemptId,
    ClientCertificateNativePromptStatus Status,
    int? PublicErrorCode = null);

internal static class NativePromptProtocol
{
    private const uint Magic = 0x504E5755;
    private const byte Version = 1;
    private const byte ArmRequestMessage = 1;
    private const byte ArmResponseMessage = 2;
    private const byte CompletionMessage = 3;
    private const byte HealthRequestMessage = 4;
    private const byte HealthResponseMessage = 5;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16 = new(false, false, true);

    public const int MaxFrameBytes = 16 * 1024;
    public const int ProtocolVersion = Version;
    public const int HeaderBytes = 12;
    public const int MaxPayloadBytes = MaxFrameBytes - HeaderBytes;
    public const int MaxPinCharacters = 128;

    public static ValueTask WriteHealthRequestAsync(
        Stream stream,
        CancellationToken cancellationToken) =>
        WriteFrameAsync(stream, HealthRequestMessage, [], cancellationToken);

    public static async ValueTask ReadHealthRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var payload = await ReadFrameAsync(
            stream,
            HealthRequestMessage,
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (payload.Length != 0)
            {
                throw new InvalidDataException("Native prompt health request is malformed.");
            }
        }
        finally
        {
            Array.Clear(payload, 0, payload.Length);
        }
    }

    public static ValueTask WriteHealthResponseAsync(
        Stream stream,
        NativePromptBrokerHealthResult health,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(health);
        var payload = new byte[sizeof(int) + sizeof(byte)];
        BinaryPrimitives.WriteInt32LittleEndian(payload, health.ProtocolVersion);
        payload[sizeof(int)] = health.IsHealthy ? (byte)1 : (byte)0;
        return WriteHealthResponseAndClearAsync(stream, payload, cancellationToken);
    }

    public static async ValueTask<NativePromptBrokerHealthResult> ReadHealthResponseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var payload = await ReadFrameAsync(
            stream,
            HealthResponseMessage,
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (payload.Length != sizeof(int) + sizeof(byte) || payload[sizeof(int)] > 1)
            {
                throw new InvalidDataException("Native prompt health response is malformed.");
            }

            return new(
                payload[sizeof(int)] == 1,
                BinaryPrimitives.ReadInt32LittleEndian(payload));
        }
        finally
        {
            Array.Clear(payload, 0, payload.Length);
        }
    }

    private static async ValueTask WriteHealthResponseAndClearAsync(
        Stream stream,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteFrameAsync(
                stream,
                HealthResponseMessage,
                payload,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(payload, 0, payload.Length);
        }
    }

    public static async ValueTask WriteArmRequestAsync(
        Stream stream,
        string policyId,
        string policyHash,
        NativePromptAttemptId attemptId,
        char[] pinBuffer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(pinBuffer);
        if (pinBuffer.Length > MaxPinCharacters)
        {
            throw new InvalidDataException("Native prompt PIN exceeds the protocol limit.");
        }

        byte[] policyBytes = [];
        byte[] hashBytes = [];
        byte[] pinBytes = [];
        byte[] payload = [];
        try
        {
            policyBytes = EncodeRequiredString(policyId);
            hashBytes = EncodeRequiredString(policyHash);
            try
            {
                pinBytes = StrictUtf16.GetBytes(pinBuffer);
            }
            catch (EncoderFallbackException)
            {
                throw new InvalidDataException("Native prompt PIN encoding is invalid.");
            }

            var payloadLength = checked(
                sizeof(ushort) + policyBytes.Length +
                sizeof(ushort) + hashBytes.Length +
                AttemptEncodedLength(attemptId) +
                sizeof(ushort) + pinBytes.Length);
            EnsurePayloadLength(payloadLength);
            payload = new byte[payloadLength];
            var offset = 0;
            WriteLengthPrefixedBytes(payload, ref offset, policyBytes);
            WriteLengthPrefixedBytes(payload, ref offset, hashBytes);
            WriteAttempt(payload, ref offset, attemptId);
            BinaryPrimitives.WriteUInt16LittleEndian(
                payload.AsSpan(offset),
                checked((ushort)pinBuffer.Length));
            offset += sizeof(ushort);
            pinBytes.CopyTo(payload, offset);

            await WriteFrameAsync(
                stream,
                ArmRequestMessage,
                payload,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Native prompt frame exceeds the protocol limit.", exception);
        }
        finally
        {
            Array.Clear(policyBytes, 0, policyBytes.Length);
            Array.Clear(hashBytes, 0, hashBytes.Length);
            Array.Clear(pinBytes, 0, pinBytes.Length);
            Array.Clear(payload, 0, payload.Length);
        }
    }

    public static async ValueTask<NativePromptArmRequest> ReadArmRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var payload = await ReadFrameAsync(
            stream,
            ArmRequestMessage,
            cancellationToken).ConfigureAwait(false);
        try
        {
            return ParseArmRequest(payload);
        }
        finally
        {
            Array.Clear(payload, 0, payload.Length);
        }
    }

    public static ValueTask WriteArmResponseAsync(
        Stream stream,
        NativePromptArmResponse response,
        CancellationToken cancellationToken) =>
        WriteResponseAsync(
            stream,
            ArmResponseMessage,
            response.AttemptId,
            EncodeArmStatus(response.Status),
            response.PublicErrorCode,
            cancellationToken);

    public static async ValueTask<NativePromptArmResponse> ReadArmResponseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var payload = await ReadFrameAsync(
            stream,
            ArmResponseMessage,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var parsed = ParseResponse(payload);
            return new(
                parsed.AttemptId,
                DecodeArmStatus(parsed.Status),
                parsed.PublicErrorCode);
        }
        finally
        {
            Array.Clear(payload, 0, payload.Length);
        }
    }

    public static ValueTask WriteCompletionAsync(
        Stream stream,
        NativePromptCompletionResponse response,
        CancellationToken cancellationToken) =>
        WriteResponseAsync(
            stream,
            CompletionMessage,
            response.AttemptId,
            EncodeCompletionStatus(response.Status),
            response.PublicErrorCode,
            cancellationToken);

    public static async ValueTask<NativePromptCompletionResponse> ReadCompletionAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var payload = await ReadFrameAsync(
            stream,
            CompletionMessage,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var parsed = ParseResponse(payload);
            return new(
                parsed.AttemptId,
                DecodeCompletionStatus(parsed.Status),
                parsed.PublicErrorCode);
        }
        finally
        {
            Array.Clear(payload, 0, payload.Length);
        }
    }

    private static NativePromptArmRequest ParseArmRequest(byte[] payload)
    {
        char[] pin = [];
        try
        {
            var reader = new PayloadReader(payload);
            var policyId = reader.ReadRequiredString();
            var policyHash = reader.ReadRequiredString();
            var attemptId = reader.ReadAttempt();
            var pinLength = reader.ReadUInt16();
            if (pinLength > MaxPinCharacters)
            {
                throw new InvalidDataException("Native prompt PIN exceeds the protocol limit.");
            }

            var pinBytes = reader.ReadBytes(checked(pinLength * sizeof(char)));
            reader.RequireEnd();
            pin = new char[pinLength];
            try
            {
                var decoded = StrictUtf16.GetChars(pinBytes, pin);
                if (decoded != pinLength)
                {
                    throw new InvalidDataException("Native prompt PIN encoding is malformed.");
                }
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidDataException("Native prompt PIN encoding is malformed.");
            }

            var request = new NativePromptArmRequest(policyId, policyHash, attemptId, pin);
            pin = [];
            return request;
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Native prompt payload is malformed.", exception);
        }
        finally
        {
            Array.Clear(pin, 0, pin.Length);
        }
    }

    private static ParsedResponse ParseResponse(byte[] payload)
    {
        var reader = new PayloadReader(payload);
        var attemptId = reader.ReadAttempt();
        var status = reader.ReadInt32();
        var errorMarker = reader.ReadByte();
        int? publicErrorCode = errorMarker switch
        {
            0 => null,
            1 => reader.ReadInt32(),
            _ => throw new InvalidDataException("Native prompt error marker is malformed.")
        };
        reader.RequireEnd();
        return new(attemptId, status, publicErrorCode);
    }

    private static async ValueTask WriteResponseAsync(
        Stream stream,
        byte messageType,
        NativePromptAttemptId attemptId,
        int status,
        int? publicErrorCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var payload = new byte[
            AttemptEncodedLength(attemptId) +
            sizeof(int) + sizeof(byte) +
            (publicErrorCode.HasValue ? sizeof(int) : 0)];
        try
        {
            var offset = 0;
            WriteAttempt(payload, ref offset, attemptId);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset), status);
            offset += sizeof(int);
            payload[offset++] = publicErrorCode.HasValue ? (byte)1 : (byte)0;
            if (publicErrorCode is int errorCode)
            {
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset), errorCode);
            }

            await WriteFrameAsync(
                stream,
                messageType,
                payload,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(payload, 0, payload.Length);
        }
    }

    private static async ValueTask WriteFrameAsync(
        Stream stream,
        byte messageType,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        EnsureKnownMessageType(messageType);
        EnsurePayloadLength(payload.Length);
        var frame = new byte[checked(HeaderBytes + payload.Length)];
        try
        {
            BinaryPrimitives.WriteUInt32LittleEndian(frame, Magic);
            frame[4] = Version;
            frame[5] = messageType;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), 0);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), payload.Length);
            payload.CopyTo(frame, HeaderBytes);
            await stream.WriteAsync(frame.AsMemory(), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(frame, 0, frame.Length);
        }
    }

    private static async ValueTask<byte[]> ReadFrameAsync(
        Stream stream,
        byte expectedMessageType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[HeaderBytes];
        byte[] payload = [];
        try
        {
            await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
            {
                throw new InvalidDataException("Native prompt frame magic is invalid.");
            }

            if (header[4] != Version)
            {
                throw new InvalidDataException("Native prompt protocol version is unsupported.");
            }

            var messageType = header[5];
            EnsureKnownMessageType(messageType);
            if (messageType != expectedMessageType)
            {
                throw new InvalidDataException("Native prompt response type is unexpected.");
            }

            if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6)) != 0)
            {
                throw new InvalidDataException("Native prompt frame header is malformed.");
            }

            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
            EnsurePayloadLength(payloadLength);
            payload = new byte[payloadLength];
            await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
            var result = payload;
            payload = [];
            return result;
        }
        finally
        {
            Array.Clear(header, 0, header.Length);
            Array.Clear(payload, 0, payload.Length);
        }
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(offset, buffer.Length - offset),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Native prompt frame was truncated.");
            }

            offset += read;
        }
    }

    private static byte[] EncodeRequiredString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException("Native prompt policy metadata is missing.");
        }

        try
        {
            var bytes = StrictUtf8.GetBytes(value);
            if (bytes.Length > ushort.MaxValue)
            {
                Array.Clear(bytes, 0, bytes.Length);
                throw new InvalidDataException("Native prompt policy metadata exceeds the protocol limit.");
            }

            return bytes;
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException("Native prompt policy metadata is malformed.", exception);
        }
    }

    private static int AttemptEncodedLength(NativePromptAttemptId attemptId) =>
        sizeof(long) + sizeof(long) + sizeof(byte) +
        (attemptId.BrowserOperationId.HasValue ? sizeof(long) : 0);

    private static void WriteAttempt(
        byte[] destination,
        ref int offset,
        NativePromptAttemptId attemptId)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination.AsSpan(offset), attemptId.NonceHigh);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt64LittleEndian(destination.AsSpan(offset), attemptId.NonceLow);
        offset += sizeof(long);
        destination[offset++] = attemptId.BrowserOperationId.HasValue ? (byte)1 : (byte)0;
        if (attemptId.BrowserOperationId is long browserOperationId)
        {
            BinaryPrimitives.WriteInt64LittleEndian(destination.AsSpan(offset), browserOperationId);
            offset += sizeof(long);
        }
    }

    private static void WriteLengthPrefixedBytes(
        byte[] destination,
        ref int offset,
        byte[] value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset), checked((ushort)value.Length));
        offset += sizeof(ushort);
        value.CopyTo(destination, offset);
        offset += value.Length;
    }

    private static void EnsurePayloadLength(int payloadLength)
    {
        if (payloadLength < 0 || payloadLength > MaxPayloadBytes)
        {
            throw new InvalidDataException("Native prompt frame length is invalid.");
        }
    }

    private static void EnsureKnownMessageType(byte messageType)
    {
        if (messageType is not (
                ArmRequestMessage or
                ArmResponseMessage or
                CompletionMessage or
                HealthRequestMessage or
                HealthResponseMessage))
        {
            throw new InvalidDataException("Native prompt message type is unknown.");
        }
    }

    private static int EncodeArmStatus(ClientCertificateNativePromptArmStatus status) => status switch
    {
        ClientCertificateNativePromptArmStatus.Armed => 0,
        ClientCertificateNativePromptArmStatus.BrokerUnavailable => 1,
        ClientCertificateNativePromptArmStatus.ProtocolMismatch => 2,
        ClientCertificateNativePromptArmStatus.PeerIdentityMismatch => 3,
        ClientCertificateNativePromptArmStatus.PolicyMismatch => 4,
        ClientCertificateNativePromptArmStatus.Replayed => 5,
        ClientCertificateNativePromptArmStatus.TimedOut => 6,
        ClientCertificateNativePromptArmStatus.Cancelled => 7,
        ClientCertificateNativePromptArmStatus.UnknownFailure => 8,
        _ => throw new InvalidDataException("Native prompt arm status is unknown.")
    };

    private static ClientCertificateNativePromptArmStatus DecodeArmStatus(int status) => status switch
    {
        0 => ClientCertificateNativePromptArmStatus.Armed,
        1 => ClientCertificateNativePromptArmStatus.BrokerUnavailable,
        2 => ClientCertificateNativePromptArmStatus.ProtocolMismatch,
        3 => ClientCertificateNativePromptArmStatus.PeerIdentityMismatch,
        4 => ClientCertificateNativePromptArmStatus.PolicyMismatch,
        5 => ClientCertificateNativePromptArmStatus.Replayed,
        6 => ClientCertificateNativePromptArmStatus.TimedOut,
        7 => ClientCertificateNativePromptArmStatus.Cancelled,
        8 => ClientCertificateNativePromptArmStatus.UnknownFailure,
        _ => throw new InvalidDataException("Native prompt arm status is unknown.")
    };

    private static int EncodeCompletionStatus(ClientCertificateNativePromptStatus status) => status switch
    {
        ClientCertificateNativePromptStatus.Success => 0,
        ClientCertificateNativePromptStatus.TargetMismatch => 1,
        ClientCertificateNativePromptStatus.FocusLost => 2,
        ClientCertificateNativePromptStatus.InputFailed => 3,
        ClientCertificateNativePromptStatus.BrokerUnavailable => 4,
        ClientCertificateNativePromptStatus.ProtocolMismatch => 5,
        ClientCertificateNativePromptStatus.PeerIdentityMismatch => 6,
        ClientCertificateNativePromptStatus.PolicyMismatch => 7,
        ClientCertificateNativePromptStatus.Replayed => 8,
        ClientCertificateNativePromptStatus.TimedOut => 9,
        ClientCertificateNativePromptStatus.Cancelled => 10,
        ClientCertificateNativePromptStatus.UnknownFailure => 11,
        _ => throw new InvalidDataException("Native prompt completion status is unknown.")
    };

    private static ClientCertificateNativePromptStatus DecodeCompletionStatus(int status) => status switch
    {
        0 => ClientCertificateNativePromptStatus.Success,
        1 => ClientCertificateNativePromptStatus.TargetMismatch,
        2 => ClientCertificateNativePromptStatus.FocusLost,
        3 => ClientCertificateNativePromptStatus.InputFailed,
        4 => ClientCertificateNativePromptStatus.BrokerUnavailable,
        5 => ClientCertificateNativePromptStatus.ProtocolMismatch,
        6 => ClientCertificateNativePromptStatus.PeerIdentityMismatch,
        7 => ClientCertificateNativePromptStatus.PolicyMismatch,
        8 => ClientCertificateNativePromptStatus.Replayed,
        9 => ClientCertificateNativePromptStatus.TimedOut,
        10 => ClientCertificateNativePromptStatus.Cancelled,
        11 => ClientCertificateNativePromptStatus.UnknownFailure,
        _ => throw new InvalidDataException("Native prompt completion status is unknown.")
    };

    private readonly record struct ParsedResponse(
        NativePromptAttemptId AttemptId,
        int Status,
        int? PublicErrorCode);

    private ref struct PayloadReader
    {
        private readonly ReadOnlySpan<byte> _payload;
        private int _offset;

        public PayloadReader(ReadOnlySpan<byte> payload)
        {
            _payload = payload;
            _offset = 0;
        }

        public byte ReadByte()
        {
            Require(sizeof(byte));
            return _payload[_offset++];
        }

        public ushort ReadUInt16()
        {
            Require(sizeof(ushort));
            var value = BinaryPrimitives.ReadUInt16LittleEndian(_payload[_offset..]);
            _offset += sizeof(ushort);
            return value;
        }

        public int ReadInt32()
        {
            Require(sizeof(int));
            var value = BinaryPrimitives.ReadInt32LittleEndian(_payload[_offset..]);
            _offset += sizeof(int);
            return value;
        }

        public long ReadInt64()
        {
            Require(sizeof(long));
            var value = BinaryPrimitives.ReadInt64LittleEndian(_payload[_offset..]);
            _offset += sizeof(long);
            return value;
        }

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            Require(count);
            var value = _payload.Slice(_offset, count);
            _offset += count;
            return value;
        }

        public string ReadRequiredString()
        {
            var bytes = ReadBytes(ReadUInt16());
            string value;
            try
            {
                value = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("Native prompt policy metadata is malformed.", exception);
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException("Native prompt policy metadata is missing.");
            }

            return value;
        }

        public NativePromptAttemptId ReadAttempt()
        {
            var nonceHigh = ReadInt64();
            var nonceLow = ReadInt64();
            var browserMarker = ReadByte();
            var browserOperationId = browserMarker switch
            {
                0 => (long?)null,
                1 => ReadInt64(),
                _ => throw new InvalidDataException("Native prompt attempt identity is malformed.")
            };
            return new(nonceHigh, nonceLow, browserOperationId);
        }

        public void RequireEnd()
        {
            if (_offset != _payload.Length)
            {
                throw new InvalidDataException("Native prompt payload contains trailing data.");
            }
        }

        private void Require(int count)
        {
            if (count < 0 || _offset > _payload.Length - count)
            {
                throw new InvalidDataException("Native prompt payload is truncated.");
            }
        }
    }
}

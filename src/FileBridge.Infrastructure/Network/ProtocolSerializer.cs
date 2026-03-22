namespace FileBridge.Infrastructure.Network;

using System.IO;
using System.Text;
using FileBridge.Domain.Enums;
using FileBridge.Domain.ValueObjects;

public sealed class ProtocolSerializer
{
    public const byte ProtocolVersion = 1;
    public const int MaxChunkSize = 65536;

    public byte[] Serialize(ProtocolMessage message)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(message.Version);
        writer.Write((byte)message.Type);
        writer.Write(message.Flags);
        writer.Write(message.Length);

        if (message.Payload.Length > 0)
            writer.Write(message.Payload);

        writer.Flush();

        // Compute CRC32 of payload
        var checksum = Crc32.Compute(message.Payload);
        writer.Write(checksum);

        return ms.ToArray();
    }

    public ProtocolMessage? Deserialize(byte[] data)
    {
        if (data.Length < 9) // min header size (1+1+2+4+1=9)
            return null;

        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var version = reader.ReadByte();
        if (version != ProtocolVersion)
            throw new InvalidDataException($"Protocol version mismatch: expected {ProtocolVersion}, got {version}");

        var type = (MessageType)reader.ReadByte();
        var flags = reader.ReadUInt16();
        var length = reader.ReadInt32();

        // Validate length
        var remaining = data.Length - 9;
        if (length > remaining)
            return null;

        var payload = length > 0 ? reader.ReadBytes(length) : Array.Empty<byte>();

        // Read and verify checksum
        var storedChecksum = reader.ReadUInt32();
        var computedChecksum = Crc32.Compute(payload);
        if (storedChecksum != computedChecksum)
            throw new InvalidDataException("CRC32 checksum verification failed.");

        return new ProtocolMessage
        {
            Version = version,
            Type = type,
            Flags = flags,
            Length = length,
            Payload = payload
        };
    }

    // Payload builders for each message type

    public byte[] BuildHandshakePayload(string deviceName, byte[] publicKey)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(ProtocolVersion);

        var nameBytes = Encoding.UTF8.GetBytes(deviceName);
        writer.Write((byte)nameBytes.Length);
        writer.Write(nameBytes);
        writer.Write(publicKey);

        return ms.ToArray();
    }

    public (string DeviceName, byte[] PublicKey) ParseHandshakePayload(byte[] payload)
    {
        using var ms = new MemoryStream(payload);
        using var reader = new BinaryReader(ms);

        var version = reader.ReadByte();
        var nameLen = reader.ReadByte();
        var nameBytes = reader.ReadBytes(nameLen);
        var publicKey = reader.ReadBytes((int)(payload.Length - 2 - nameLen));

        return (Encoding.UTF8.GetString(nameBytes), publicKey);
    }

    public byte[] BuildConnectPayload(Guid deviceId, string pairingToken)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        var deviceIdBytes = deviceId.ToByteArray();
        writer.Write(deviceIdBytes);
        var tokenBytes = Encoding.UTF8.GetBytes(pairingToken);
        writer.Write(tokenBytes);

        return ms.ToArray();
    }

    public byte[] BuildListFilesPayload(IReadOnlyList<(string Name, long Size, string MimeType)> files)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(files.Count);
        foreach (var (name, size, mimeType) in files)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            writer.Write((byte)nameBytes.Length);
            writer.Write(nameBytes);
            writer.Write(size);

            var mimeBytes = Encoding.UTF8.GetBytes(mimeType);
            writer.Write((byte)mimeBytes.Length);
            writer.Write(mimeBytes);
        }

        return ms.ToArray();
    }

    public byte[] BuildFileRequestPayload(Guid fileItemId, string fileName, long fileSize, int chunkIndex, bool resume, long resumeOffset)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(fileItemId.ToByteArray());
        var nameBytes = Encoding.UTF8.GetBytes(fileName);
        writer.Write(nameBytes);
        writer.Write(fileSize);
        writer.Write(chunkIndex);
        writer.Write(resume);
        writer.Write(resumeOffset);

        return ms.ToArray();
    }

    public byte[] BuildDataPayload(int chunkIndex, long offset, ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(chunkIndex);
        writer.Write(offset);
        writer.Write(data.Length);
        writer.Write(data.ToArray());

        var checksum = Crc32.Compute(data);
        writer.Write(checksum);

        return ms.ToArray();
    }

    public (int ChunkIndex, long Offset, byte[] Data, uint Checksum) ParseDataPayload(byte[] payload)
    {
        using var ms = new MemoryStream(payload);
        using var reader = new BinaryReader(ms);

        var chunkIndex = reader.ReadInt32();
        var offset = reader.ReadInt64();
        var dataLen = reader.ReadInt32();
        var data = reader.ReadBytes(dataLen);
        var checksum = reader.ReadUInt32();

        return (chunkIndex, offset, data, checksum);
    }

    public byte[] BuildPausePayload(Guid jobId, long bytesTransferred)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(jobId.ToByteArray());
        writer.Write(bytesTransferred);

        return ms.ToArray();
    }

    public (Guid JobId, long BytesTransferred) ParsePausePayload(byte[] payload)
    {
        using var ms = new MemoryStream(payload);
        using var reader = new BinaryReader(ms);

        var jobId = new Guid(reader.ReadBytes(16));
        var bytesTransferred = reader.ReadInt64();

        return (jobId, bytesTransferred);
    }

    public byte[] BuildResumePayload(Guid jobId, long bytesTransferred)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(jobId.ToByteArray());
        writer.Write(bytesTransferred);

        return ms.ToArray();
    }

    public byte[] BuildAckPayload(ulong messageId, byte status)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(messageId);
        writer.Write(status);

        return ms.ToArray();
    }

    public (ulong MessageId, byte Status) ParseAckPayload(byte[] payload)
    {
        using var ms = new MemoryStream(payload);
        using var reader = new BinaryReader(ms);

        var messageId = reader.ReadUInt64();
        var status = reader.ReadByte();

        return (messageId, status);
    }

    public byte[] BuildErrorPayload(ushort code, string message)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(code);
        var msgBytes = Encoding.UTF8.GetBytes(message);
        writer.Write((byte)msgBytes.Length);
        writer.Write(msgBytes);

        return ms.ToArray();
    }
}

public sealed class ProtocolMessage
{
    public byte Version { get; set; } = ProtocolSerializer.ProtocolVersion;
    public MessageType Type { get; set; }
    public ushort Flags { get; set; }
    public int Length { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();

    public bool IsEncrypted => (Flags & 0x01) != 0;
    public void SetEncrypted(bool value)
    {
        if (value) Flags |= 0x01;
        else Flags = (ushort)(Flags & ~0x01);
    }
}

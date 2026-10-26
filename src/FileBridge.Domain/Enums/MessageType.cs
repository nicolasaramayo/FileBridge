namespace FileBridge.Domain.Enums;

public enum MessageType : byte
{
    Handshake = 0x01,
    Connect = 0x02,
    ListFiles = 0x03,
    FileRequest = 0x04,
    Data = 0x05,
    Pause = 0x06,
    Resume = 0x07,
    Ack = 0x08,
    Error = 0x09,
    Disconnect = 0x0A,

    /// <summary>Sender → receiver after the last Data chunk: transfer id + SHA-256 of the whole file.
    /// The receiver answers Ack (hash and size verified) or Error.</summary>
    TransferComplete = 0x0B
}

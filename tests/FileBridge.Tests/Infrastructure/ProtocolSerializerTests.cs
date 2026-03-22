using FileBridge.Infrastructure.Network;
using FileBridge.Domain.Enums;

namespace FileBridge.Tests.Infrastructure;

[TestClass]
public class ProtocolSerializerTests
{
    private ProtocolSerializer _serializer = null!;
    
    [TestInitialize]
    public void Setup()
    {
        _serializer = new ProtocolSerializer();
    }
    
    [TestMethod]
    public void Serialize_ShouldCreateValidMessage()
    {
        // Arrange
        var message = new ProtocolMessage
        {
            Type = MessageType.Handshake,
            Payload = System.Text.Encoding.UTF8.GetBytes("Hello")
        };
        
        // Act
        var data = _serializer.Serialize(message);
        
        // Assert
        Assert.IsNotNull(data);
        Assert.IsTrue(data.Length > 0);
    }
    
    [TestMethod]
    public void Deserialize_ShouldRecoverOriginalMessage()
    {
        // Arrange
        var original = new ProtocolMessage
        {
            Type = MessageType.FileRequest,
            Payload = new byte[] { 1, 2, 3, 4, 5 }
        };
        
        // Act
        var data = _serializer.Serialize(original);
        var deserialized = _serializer.Deserialize(data);
        
        // Assert
        Assert.IsNotNull(deserialized);
        Assert.AreEqual(original.Type, deserialized.Type);
        Assert.IsTrue(original.Payload.SequenceEqual(deserialized.Payload));
    }
    
    [TestMethod]
    public void SerializeDeserialize_ShouldMaintainMessageType()
    {
        // Arrange & Act & Assert
        var types = new[] 
        { 
            MessageType.Handshake, 
            MessageType.Connect, 
            MessageType.ListFiles,
            MessageType.FileRequest,
            MessageType.Data,
            MessageType.Ack,
            MessageType.Pause,
            MessageType.Resume,
            MessageType.Error
        };
        
        foreach (var type in types)
        {
            var message = new ProtocolMessage { Type = type, Payload = new byte[] { 0 } };
            var data = _serializer.Serialize(message);
            var result = _serializer.Deserialize(data);
            
            Assert.AreEqual(type, result?.Type);
        }
    }
    
    [TestMethod]
    public void Deserialize_ShouldReturnNull_ForInvalidData()
    {
        // Arrange - too short data
        var invalidData = new byte[] { 1, 2 };
        
        // Act
        var result = _serializer.Deserialize(invalidData);
        
        // Assert
        Assert.IsNull(result);
    }
    
    [TestMethod]
    public void Deserialize_ShouldThrow_ForVersionMismatch()
    {
        // Arrange
        var message = new ProtocolMessage
        {
            Version = 99, // Invalid version
            Type = MessageType.Handshake,
            Payload = new byte[] { 1 }
        };
        var data = _serializer.Serialize(message);
        
        // Act & Assert
        InvalidDataException? ex = null;
        try
        {
            _serializer.Deserialize(data);
        }
        catch (InvalidDataException e)
        {
            ex = e;
        }
        Assert.IsNotNull(ex);
    }
    
    [TestMethod]
    public void ProtocolVersion_ShouldBeOne()
    {
        Assert.AreEqual(1, ProtocolSerializer.ProtocolVersion);
    }
    
    [TestMethod]
    public void MaxChunkSize_ShouldBe65536()
    {
        Assert.AreEqual(65536, ProtocolSerializer.MaxChunkSize);
    }
    
    [TestMethod]
    public void Serialize_EmptyPayload_ShouldWork()
    {
        // Arrange
        var message = new ProtocolMessage
        {
            Type = MessageType.Pause,
            Payload = Array.Empty<byte>()
        };
        
        // Act
        var data = _serializer.Serialize(message);
        var result = _serializer.Deserialize(data);
        
        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(MessageType.Pause, result.Type);
        Assert.AreEqual(0, result.Payload.Length);
    }
    
    [TestMethod]
    public void ProtocolMessage_ShouldSupportEncryptionFlag()
    {
        // Arrange
        var message = new ProtocolMessage
        {
            Type = MessageType.Data,
            Payload = new byte[] { 1, 2, 3 }
        };
        
        // Act
        message.SetEncrypted(true);
        
        // Assert
        Assert.IsTrue(message.IsEncrypted);
    }
    
    [TestMethod]
    public void ProtocolMessage_EncryptedFlag_ShouldBeSettable()
    {
        // Arrange
        var message = new ProtocolMessage { Type = MessageType.Data };
        
        // Act & Assert
        message.SetEncrypted(true);
        Assert.IsTrue(message.IsEncrypted);
        
        message.SetEncrypted(false);
        Assert.IsFalse(message.IsEncrypted);
    }
    
    [TestMethod]
    public void BuildHandshakePayload_ShouldEncodeCorrectly()
    {
        // Arrange
        var deviceName = "TestDevice";
        var publicKey = new byte[] { 1, 2, 3, 4 };
        
        // Act
        var payload = _serializer.BuildHandshakePayload(deviceName, publicKey);
        
        // Assert
        Assert.IsNotNull(payload);
        Assert.IsTrue(payload.Length > 0);
    }
    
    [TestMethod]
    public void ParseHandshakePayload_ShouldDecodeCorrectly()
    {
        // Arrange
        var deviceName = "TestDevice";
        var publicKey = new byte[] { 1, 2, 3, 4 };
        var payload = _serializer.BuildHandshakePayload(deviceName, publicKey);
        
        // Act
        var (parsedName, parsedKey) = _serializer.ParseHandshakePayload(payload);
        
        // Assert
        Assert.AreEqual(deviceName, parsedName);
        Assert.IsTrue(parsedKey.SequenceEqual(publicKey));
    }
}

using FileBridge.Domain.Entities;
using FileBridge.Domain.Enums;

namespace FileBridge.Tests.Domain;

[TestClass]
public class DeviceTests
{
    [TestMethod]
    public void Device_ShouldInitializeWithDefaults()
    {
        var device = new Device();
        
        // Guid is auto-generated, verify it's not empty
        Assert.AreNotEqual(Guid.Empty, device.Id);
        Assert.AreEqual(DeviceRole.Unknown, device.Role);
    }
    
    [TestMethod]
    public void Device_ShouldSetProperties()
    {
        var device = new Device
        {
            Id = Guid.NewGuid(),
            Name = "Test Device",
            IpAddress = "192.168.1.100",
            Port = 45678,
            Role = DeviceRole.PC,
            PublicKey = new byte[] { 1, 2, 3, 4 }
        };
        
        Assert.AreNotEqual(Guid.Empty, device.Id);
        Assert.AreEqual("Test Device", device.Name);
        Assert.AreEqual("192.168.1.100", device.IpAddress);
        Assert.AreEqual(45678, device.Port);
        Assert.AreEqual(DeviceRole.PC, device.Role);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, device.PublicKey);
    }
    
    [TestMethod]
    public void Device_ShouldTrackPairingInfo()
    {
        var now = DateTimeOffset.UtcNow;
        var device = new Device
        {
            PairedAt = now,
            LastSeenAt = now.AddMinutes(5)
        };
        
        Assert.AreEqual(now, device.PairedAt);
        Assert.AreEqual(now.AddMinutes(5), device.LastSeenAt);
    }
    
    [TestMethod]
    public void Device_Role_ShouldBeUnknownByDefault()
    {
        var device = new Device();
        Assert.AreEqual(DeviceRole.Unknown, device.Role);
    }
}

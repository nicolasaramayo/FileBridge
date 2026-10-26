using FileBridge.Application.Services;
using FileBridge.Domain.Entities;

namespace FileBridge.Tests.Application;

[TestClass]
public class DeviceRegistryTests
{
    [TestMethod]
    public void SameEndpointWithANewId_ReplacesTheOldEntry()
    {
        // A peer that restarts (or a manual entry later found by mDNS) must not show up twice.
        var registry = new DeviceRegistry();
        var removed = new List<Device>();
        registry.DeviceRemoved += (_, d) => removed.Add(d);
        var before = new Device { Id = Guid.NewGuid(), Name = "PC", IpAddress = "192.168.1.10", Port = 45678 };
        var after = new Device { Id = Guid.NewGuid(), Name = "PC", IpAddress = "192.168.1.10", Port = 45678 };

        registry.AddOrUpdate(before);
        registry.AddOrUpdate(after);

        Assert.AreEqual(1, registry.All.Count);
        Assert.AreSame(after, registry.All.Single());
        Assert.AreSame(before, removed.Single());
        Assert.IsNull(registry.Find(before.Id));
    }

    [TestMethod]
    public void SameIdNewAddress_IsUpdatedInPlace()
    {
        var registry = new DeviceRegistry();
        var id = Guid.NewGuid();

        registry.AddOrUpdate(new Device { Id = id, IpAddress = "192.168.1.10", Port = 45678 });
        registry.AddOrUpdate(new Device { Id = id, IpAddress = "192.168.1.22", Port = 45678 });

        Assert.AreEqual("192.168.1.22", registry.Find(id)!.IpAddress);
        Assert.AreEqual(1, registry.All.Count);
    }

    [TestMethod]
    public void DifferentPortsOnTheSameHost_AreDifferentDevices()
    {
        var registry = new DeviceRegistry();

        registry.AddOrUpdate(new Device { Id = Guid.NewGuid(), IpAddress = "127.0.0.1", Port = 1000 });
        registry.AddOrUpdate(new Device { Id = Guid.NewGuid(), IpAddress = "127.0.0.1", Port = 2000 });

        Assert.AreEqual(2, registry.All.Count);
    }
}

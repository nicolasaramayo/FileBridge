using FileBridge.Infrastructure.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileBridge.Tests.Infrastructure;

[TestClass]
public class DeviceDiscoveryTests
{
    [TestMethod]
    public async Task Advertiser_And_Discovery_Should_Find_Each_Other()
    {
        string deviceName = "TestDevice_" + Guid.NewGuid().ToString().Substring(0, 8);
        string deviceId = Guid.NewGuid().ToString();
        int port = 45678;

        using var advertiser = new ZeroconfAdvertiser();
        using var discovery = new ZeroconfDiscovery();

        var tcs = new TaskCompletionSource<bool>();
        discovery.OnDeviceFound += (s, d) =>
        {
            if (d.Id.ToString() == deviceId)
            {
                tcs.TrySetResult(true);
            }
        };

        await discovery.StartDiscoveryAsync();
        await advertiser.StartAdvertisingAsync(deviceName, port, deviceId);

        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(20));
        var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

        await advertiser.StopDiscoveryAsync();
        await discovery.StopDiscoveryAsync();

        Assert.IsTrue(completedTask == tcs.Task, "Discovery failed to find the advertised device within the timeout.");
    }
}

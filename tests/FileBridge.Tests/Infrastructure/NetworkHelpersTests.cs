using System.Net;
using FileBridge.Infrastructure.Network;

namespace FileBridge.Tests.Infrastructure;

[TestClass]
public class NetworkHelpersTests
{
    private static readonly IReadOnlyList<LocalNetwork.LocalAddress> PhoneOnHomeWifi = new[]
    {
        new LocalNetwork.LocalAddress(IPAddress.Parse("192.168.1.50"), IPAddress.Parse("255.255.255.0"), "wlan0")
    };

    [TestMethod]
    public void ChooseBestAddress_PrefersTheAddressInOurSubnet()
    {
        // What a Windows PC with WSL/Hyper-V typically advertises: vEthernet first, Wi-Fi later.
        var advertised = new[] { "172.20.16.1", "fe80::1c2d:3e4f:5a6b:7c8d", "192.168.1.10" };

        Assert.AreEqual("192.168.1.10", LocalNetwork.ChooseBestAddress(advertised, PhoneOnHomeWifi));
    }

    [TestMethod]
    public void ChooseBestAddress_SkipsLinkLocalAndIpv6_AndPrefersPrivateRanges()
    {
        var advertised = new[] { "fe80::1", "169.254.10.20", "8.8.8.8", "10.0.0.7" };

        Assert.AreEqual("10.0.0.7", LocalNetwork.ChooseBestAddress(advertised, Array.Empty<LocalNetwork.LocalAddress>()));
    }

    [TestMethod]
    public void ChooseBestAddress_ReturnsNull_WhenThereIsNoUsableIpv4()
    {
        Assert.IsNull(LocalNetwork.ChooseBestAddress(new[] { "fe80::1", "127.0.0.1", "", null }, PhoneOnHomeWifi));
    }

    [TestMethod]
    public void BuildInstanceName_IsAlwaysAValidDnsLabel()
    {
        var id = Guid.NewGuid().ToString();
        foreach (var name in new[] { "DESKTOP-ABC123", "Galaxy S23 Ultra de Nicolás", "my.pc.local", "", new string('x', 200) })
        {
            var label = ZeroconfAdvertiser.BuildInstanceName(name, id);
            Assert.IsTrue(label.Length <= 63, $"'{label}' is longer than 63");
            Assert.IsTrue(label.EndsWith(id), label);
            Assert.IsTrue(label.All(c => c < 128 && (char.IsLetterOrDigit(c) || c == '-')), $"'{label}' has invalid characters");
        }
    }

    [TestMethod]
    public void SanitizeFileName_RemovesDirectoriesAndInvalidCharacters()
    {
        Assert.AreEqual("passwd", FileReceiver.SanitizeFileName("../../etc/passwd"));
        Assert.AreEqual("y.txt", FileReceiver.SanitizeFileName(@"C:\x\y.txt"));
        Assert.AreEqual("a_b_c_.txt", FileReceiver.SanitizeFileName("a:b*c?.txt"));
        Assert.AreEqual("received-file", FileReceiver.SanitizeFileName(""));
        Assert.AreEqual("received-file", FileReceiver.SanitizeFileName(".."));
        Assert.AreEqual("foto de año.jpg", FileReceiver.SanitizeFileName("foto de año.jpg"));
    }

    [TestMethod]
    public void SanitizeFileName_AvoidsWindowsDeviceNames()
    {
        // On Windows these are devices, not files: writing "nul.txt" loses the data.
        Assert.AreEqual("_nul.txt", FileReceiver.SanitizeFileName("nul.txt"));
        Assert.AreEqual("_CON", FileReceiver.SanitizeFileName("CON"));
        Assert.AreEqual("_com1.log", FileReceiver.SanitizeFileName("com1.log"));
        Assert.AreEqual("_LPT9.tar.gz", FileReceiver.SanitizeFileName("LPT9.tar.gz"));
        Assert.AreEqual("console.txt", FileReceiver.SanitizeFileName("console.txt"));
        Assert.AreEqual("nul2.txt", FileReceiver.SanitizeFileName("nul2.txt"));
    }
}

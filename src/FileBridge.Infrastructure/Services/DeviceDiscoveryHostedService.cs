namespace FileBridge.Infrastructure.Services;

using System.Threading.Channels;
using FileBridge.Application.Services;
using FileBridge.Domain.Entities;
using Microsoft.Extensions.Logging;

public class DeviceDiscoveryHostedService : FileBridgeHostedService
{
    private readonly DeviceDiscoveryService _discoveryService;
    private readonly ILogger<DeviceDiscoveryHostedService> _logger;
    private readonly Channel<Device> _deviceChannel;

    public DeviceDiscoveryHostedService(
        DeviceDiscoveryService discoveryService,
        ILogger<DeviceDiscoveryHostedService> logger)
    {
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _deviceChannel = Channel.CreateBounded<Device>(new BoundedChannelOptions(10)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    public ChannelReader<Device> Devices => _deviceChannel.Reader;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Device discovery service starting");

        try
        {
            await _discoveryService.StartDiscoveryAsync(stoppingToken);

            await foreach (var device in _discoveryService.GetDiscoveredDevicesAsync(stoppingToken))
            {
                await _deviceChannel.Writer.WriteAsync(device, stoppingToken);
                _logger.LogInformation("Discovered device: {DeviceName} at {IpAddress}",
                    device.Name, device.IpAddress);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Device discovery service stopping");
        }
        finally
        {
            await _discoveryService.StopDiscoveryAsync();
            _deviceChannel.Writer.Complete();
        }
    }
}

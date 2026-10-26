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

    /// <summary>
    /// Discovery at startup only runs this long, so devices are already listed when the Devices page
    /// opens without scanning every 10 s for as long as the process lives (battery). The Devices page
    /// scans again while it is visible.
    /// </summary>
    public static TimeSpan InitialDiscoveryDuration { get; set; } = TimeSpan.FromMinutes(2);

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

        using var initialRun = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        initialRun.CancelAfter(InitialDiscoveryDuration);

        try
        {
            await _discoveryService.StartDiscoveryAsync(initialRun.Token);

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
            _deviceChannel.Writer.TryComplete();
        }
    }
}

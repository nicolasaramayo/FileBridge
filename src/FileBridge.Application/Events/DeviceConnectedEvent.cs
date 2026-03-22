namespace FileBridge.Application.Events;

public record DeviceConnectedEvent(
    string DeviceId,
    string DeviceName,
    string IpAddress,
    int Port,
    DateTimeOffset ConnectedAt
);
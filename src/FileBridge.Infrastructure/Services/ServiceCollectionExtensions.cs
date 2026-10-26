namespace FileBridge.Infrastructure.Services;

using FileBridge.Application.Services;
using FileBridge.Domain.Interfaces;
using FileBridge.Infrastructure.Network;
using FileBridge.Infrastructure.Persistence;
using FileBridge.Infrastructure.Persistence.Repositories;
using FileBridge.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFileBridgeInfrastructureServices(
        this IServiceCollection services,
        string dbPath,
        Action<FileBridgeOptions>? configure = null)
    {
        var options = new FileBridgeOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Database
        services.AddSingleton(new FileBridgeDbContext(dbPath));
        services.AddSingleton<ITransferStore, SqliteTransferStore>();
        services.AddSingleton<FileBridge.Domain.Interfaces.IDeviceRepository, DeviceRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<ISettingsService, SettingsService>();

        // Security
        services.AddSingleton<IAesEncryptionService, AesEncryptionService>();
        services.AddSingleton<IKeyExchangeService, DiffieHellmanKeyExchange>();

#if ANDROID
        services.AddSingleton<ISecureStorage, AndroidSecureStorage>();
#else
        services.AddSingleton<ISecureStorage, SecureStorage>();
#endif

        // Network
        services.AddSingleton<TcpServer>();
        services.AddSingleton<TcpFileClient>();
        services.AddSingleton<FileReceiver>();
        services.AddSingleton<ConnectionManager>();   // owns TransferProtocolHandler (it was registered here but could never be resolved)
        services.AddSingleton<IFileTransport, TcpFileTransport>();
        services.AddSingleton<IDiscoveryService, ZeroconfDiscovery>();
        services.AddSingleton<ZeroconfAdvertiser>();

        // Application services
        services.AddSingleton<FileService>();
        services.AddSingleton<IFileService>(sp => sp.GetRequiredService<FileService>()); // VMs ask for both
        services.AddSingleton<IDeviceRegistry, DeviceRegistry>();
        services.AddSingleton<DeviceDiscoveryService>();
        services.AddSingleton<IPairingService, PairingService>();
        services.AddSingleton<TransferOrchestrator>();
        // Without this, ConnectionManager got null and never handled a single message.
        services.AddSingleton<ITransferService>(sp => sp.GetRequiredService<TransferOrchestrator>());

        // Background services: one instance, reachable both as hosted service and from the UI.
        // NOTE: .NET MAUI does not start IHostedService by itself; the app must start them (see UI/Services/HostedServicesStarter).
        services.AddSingleton<TransferBackgroundService>();
        services.AddSingleton<ITransferBackgroundService>(sp => sp.GetRequiredService<TransferBackgroundService>());
        services.AddHostedService(sp => sp.GetRequiredService<TransferBackgroundService>());
        services.AddHostedService<DeviceDiscoveryHostedService>();

        return services;
    }
}

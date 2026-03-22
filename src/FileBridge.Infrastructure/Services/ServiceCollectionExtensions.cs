namespace FileBridge.Infrastructure.Services;

using FileBridge.Application.Services;
using FileBridge.Domain.Interfaces;
using FileBridge.Infrastructure.Network;
using FileBridge.Infrastructure.Persistence;
using FileBridge.Infrastructure.Persistence.Repositories;
using FileBridge.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFileBridgeInfrastructureServices(
        this IServiceCollection services,
        string dbPath)
    {
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
        services.AddSingleton<ConnectionManager>();
        services.AddSingleton<TransferProtocolHandler>();
        services.AddSingleton<IDiscoveryService, ZeroconfDiscovery>();
        services.AddSingleton<ZeroconfAdvertiser>();

        // Application services
        services.AddSingleton<IFileService, FileService>();
        services.AddSingleton<DeviceDiscoveryService>();
        services.AddSingleton<IPairingService, PairingService>();
        services.AddSingleton<TransferOrchestrator>();

        // Background Services
        services.AddHostedService<DeviceDiscoveryHostedService>();
        services.AddHostedService<TransferBackgroundService>();

        return services;
    }
}

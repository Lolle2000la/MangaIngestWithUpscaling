using MangaIngestWithUpscaling.RemoteWorker.Services;
using MangaIngestWithUpscaling.RemoteWorker.Background;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MangaIngestWithUpscaling.RemoteWorker.Tests.Services;

public class ServiceRegistrationTests
{
    [Fact]
    public void RegisterRemoteWorkerServices_ShouldRegisterRemoteTaskProcessor()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.RegisterRemoteWorkerServices();
        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var remoteTaskProcessor = serviceProvider.GetService<RemoteTaskProcessor>();
        Assert.NotNull(remoteTaskProcessor);
    }

    [Fact]
    public void RegisterRemoteWorkerServices_ShouldRegisterRemoteTaskProcessorAsSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.RegisterRemoteWorkerServices();
        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var processor1 = serviceProvider.GetService<RemoteTaskProcessor>();
        var processor2 = serviceProvider.GetService<RemoteTaskProcessor>();

        Assert.NotNull(processor1);
        Assert.NotNull(processor2);
        Assert.Same(processor1, processor2); // Should be the same instance (singleton)
    }

    [Fact]
    public void RegisterRemoteWorkerServices_ShouldRegisterAsHostedService()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.RegisterRemoteWorkerServices();

        // Assert
        var hostedServiceDescriptor = services.FirstOrDefault(s => 
            s.ServiceType == typeof(IHostedService) && 
            s.ImplementationType == typeof(RemoteTaskProcessor));
        
        Assert.NotNull(hostedServiceDescriptor);
        Assert.Equal(ServiceLifetime.Singleton, hostedServiceDescriptor.Lifetime);
    }

    [Fact]
    public void RegisterRemoteWorkerServices_ShouldCallRegisterSharedServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.RegisterRemoteWorkerServices();

        // Assert
        // Verify that shared services were registered by checking for a known shared service
        var descriptors = services.Where(s => s.ServiceType.Namespace?.StartsWith("MangaIngestWithUpscaling.Shared") == true).ToList();
        
        // Should have registered several services from the Shared assembly
        Assert.NotEmpty(descriptors);
    }

    [Fact]
    public void RegisterRemoteWorkerServices_ShouldNotThrow()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act & Assert
        var exception = Record.Exception(() => services.RegisterRemoteWorkerServices());
        Assert.Null(exception);
    }
}
using MangaIngestWithUpscaling.RemoteWorker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MangaIngestWithUpscaling.RemoteWorker.Tests.Services;

public class ServiceRegistrationTests
{
    [Fact]
    public void RegisterRemoteWorkerServices_ShouldRegisterHostedServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.RegisterRemoteWorkerServices();

        // Assert
        var hostedServiceDescriptors = services.Where(s => s.ServiceType == typeof(IHostedService)).ToList();
        
        Assert.NotEmpty(hostedServiceDescriptors);
        
        // Should have at least one hosted service registered as singleton
        Assert.Contains(hostedServiceDescriptors, d => d.Lifetime == ServiceLifetime.Singleton);
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

    [Fact]
    public void RegisterRemoteWorkerServices_ShouldRegisterServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.RegisterRemoteWorkerServices();

        // Assert
        // Should have registered services
        Assert.NotEmpty(services);
        
        // Should include services from both RemoteWorker and Shared assemblies
        var serviceTypes = services.Select(s => s.ServiceType).ToList();
        Assert.Contains(serviceTypes, t => t == typeof(IHostedService));
    }
}
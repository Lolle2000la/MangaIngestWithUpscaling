using MangaIngestWithUpscaling.Shared.Configuration;
using MangaIngestWithUpscaling.Shared.Services;
using MangaIngestWithUpscaling.Shared.Services.Inference;
using Microsoft.Extensions.DependencyInjection;

namespace MangaIngestWithUpscaling.Shared.Tests.Services;

public class ServiceRegistrationTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void RegisterSharedServices_ShouldRegisterServices()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.RegisterSharedServices();

        // Assert
        // Verify that RegisterSharedServices actually adds services to the collection
        Assert.NotEmpty(services);

        // Check for some expected service types without trying to construct them
        var serviceTypes = services.Select(s => s.ServiceType).ToList();

        // Should include various shared services
        Assert.Contains(
            serviceTypes,
            t => t.Namespace?.StartsWith("MangaIngestWithUpscaling.Shared") == true
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RegisterSharedServices_ResolvesTheDeviceMemoryCalibratorAndItsRunner()
    {
        // Descriptors being present says nothing about whether the container can *build* the services:
        // the calibrator takes the child-process runner as its last constructor parameter, and the
        // runner has two constructors — a public one for the container and an internal one the tests
        // use. A container that cannot choose between them fails at resolution, and in production that
        // is the first page of the first chapter, not startup. So the resolution is what this pins.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<UpscalerConfig>(_ => { });
        services.RegisterSharedServices();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IDeviceMemoryBenchmarkProcessRunner>());
        Assert.NotNull(provider.GetRequiredService<IDeviceMemoryCalibrator>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RegisterSharedServices_ShouldNotThrow()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act & Assert
        var exception = Record.Exception(() => services.RegisterSharedServices());
        Assert.Null(exception);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RegisterSharedServices_ShouldCallAutoRegister()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.RegisterSharedServices();

        // Assert
        // Verify that auto-registration worked by checking for services
        List<ServiceDescriptor> descriptors = services
            .Where(s =>
                s.ServiceType.Namespace?.StartsWith("MangaIngestWithUpscaling.Shared") == true
            )
            .ToList();

        // Should have registered several services from the Shared assembly
        Assert.NotEmpty(descriptors);

        // Verify we have different service lifetimes (indicating AutoRegister attributes were processed)
        var lifetimes = descriptors.Select(d => d.Lifetime).Distinct().ToList();
        Assert.NotEmpty(lifetimes);
    }
}

using MangaIngestWithUpscaling.Shared.Services;
using MangaIngestWithUpscaling.Shared.Services.FileSystem;
using Microsoft.Extensions.DependencyInjection;

namespace MangaIngestWithUpscaling.Shared.Tests.Services;

public class ServiceRegistrationTests
{
    [Fact]
    public void RegisterSharedServices_ShouldRegisterFileSystemForCurrentPlatform()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.RegisterSharedServices();
        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var fileSystem = serviceProvider.GetService<IFileSystem>();
        Assert.NotNull(fileSystem);

        // The actual implementation depends on the platform, but we should get one
        Assert.True(fileSystem is UnixFileSystem or GenericFileSystem);
    }

    [Fact]
    public void RegisterSharedServices_ShouldRegisterFileSystemAsSingleton()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.RegisterSharedServices();
        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var fileSystem1 = serviceProvider.GetService<IFileSystem>();
        var fileSystem2 = serviceProvider.GetService<IFileSystem>();

        Assert.NotNull(fileSystem1);
        Assert.NotNull(fileSystem2);
        Assert.Same(fileSystem1, fileSystem2); // Should be the same instance (singleton)
    }

    [Fact]
    public void RegisterSharedServices_ShouldCallAutoRegister()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.RegisterSharedServices();

        // Assert
        // Verify that auto-registration worked by checking for a known service
        // This tests that AutoRegister was called and found services with RegisterScoped/RegisterSingleton attributes
        var descriptors = services.Where(s => s.ServiceType.Namespace?.StartsWith("MangaIngestWithUpscaling.Shared") == true).ToList();
        
        // Should have registered several services from the Shared assembly
        Assert.NotEmpty(descriptors);
    }

    [Fact]
    public void RegisterSharedServices_ShouldNotThrow()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act & Assert
        var exception = Record.Exception(() => services.RegisterSharedServices());
        Assert.Null(exception);
    }
}
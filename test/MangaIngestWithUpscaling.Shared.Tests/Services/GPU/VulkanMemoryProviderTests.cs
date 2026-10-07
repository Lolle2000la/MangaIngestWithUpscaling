using MangaIngestWithUpscaling.Shared.Services.GPU;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.GPU;

public class VulkanMemoryProviderTests
{
    [Fact]
    public void IsAvailable_DoesNotThrow()
    {
        // Calling IsAvailable should probe safely and never throw an unhandled native exception
        bool isAvailable = VulkanMemoryProvider.IsAvailable;
        Assert.True(isAvailable || !isAvailable); // Validates execution without crash
    }

    [Fact]
    public void QueryAllDevices_WhenAvailable_ReturnsValidMemoryMetrics()
    {
        if (!VulkanMemoryProvider.IsAvailable)
        {
            return; // Skip on headless environments without Vulkan drivers
        }

        var devices = VulkanMemoryProvider.QueryAllDevices();
        Assert.NotEmpty(devices);

        foreach (var dev in devices)
        {
            Assert.False(string.IsNullOrWhiteSpace(dev.DeviceName));
            Assert.True(
                dev.TotalVramBytes > 0,
                $"Device {dev.DeviceName} reports positive total VRAM"
            );
            // If the driver supports VK_EXT_memory_budget, budget should be > 0 and <= total
            if (dev.BudgetBytes > 0)
            {
                Assert.True(
                    dev.BudgetBytes <= dev.TotalVramBytes,
                    $"Budget ({dev.BudgetBytes} bytes) cannot exceed total VRAM ({dev.TotalVramBytes} bytes) for device {dev.DeviceName}"
                );
            }
        }
    }

    [Fact]
    public void QueryDevice_DefaultIndex_MatchesFirstDevice()
    {
        if (!VulkanMemoryProvider.IsAvailable)
        {
            return;
        }

        var first = VulkanMemoryProvider.QueryDevice(0);
        Assert.NotNull(first);
        Assert.True(first.TotalVramBytes > 0);
        Assert.True(first.BudgetBytes > 0);
        Assert.True(first.BudgetBytes <= first.TotalVramBytes);
        Assert.Equal(0, first.DeviceIndex);
        Assert.False(string.IsNullOrEmpty(first.DeviceName));
    }
}

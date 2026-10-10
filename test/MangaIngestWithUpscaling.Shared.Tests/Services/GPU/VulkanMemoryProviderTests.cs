using MangaIngestWithUpscaling.Shared.Services.GPU;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Services.GPU;

public class VulkanMemoryProviderTests
{
    private const string NoVulkan =
        "no Vulkan driver in this environment, so there is nothing to assert about";

    [Fact]
    public void IsAvailable_DoesNotThrow()
    {
        // Calling IsAvailable should probe safely and never throw an unhandled native exception
        _ = VulkanMemoryProvider.IsAvailable;
    }

    [Fact]
    public void QueryAllDevices_WhenAvailable_ReturnsValidMemoryMetrics()
    {
        Assert.SkipWhen(!VulkanMemoryProvider.IsAvailable, NoVulkan);

        var devices = VulkanMemoryProvider.QueryAllDevices();
        Assert.NotEmpty(devices);

        foreach (var dev in devices)
        {
            Assert.False(string.IsNullOrWhiteSpace(dev.DeviceName));
            Assert.True(
                dev.TotalVramBytes > 0,
                $"Device {dev.DeviceName} reports positive total VRAM"
            );
            // If the driver supports VK_EXT_memory_budget, budget should be > 0 and <= total. The
            // extension is not guaranteed, and a driver without it leaves the budget zeroed, so
            // this is only an upper bound when the driver reports a budget at all.
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
        Assert.SkipWhen(!VulkanMemoryProvider.IsAvailable, NoVulkan);

        var first = VulkanMemoryProvider.QueryDevice(0);
        Assert.NotNull(first);
        Assert.True(first.TotalVramBytes > 0);
        // The budget requires VK_EXT_memory_budget; its absence is a legitimate "unknown", not a
        // failure, so only the relationship is asserted when the driver reports one.
        if (first.BudgetBytes > 0)
        {
            Assert.True(first.BudgetBytes <= first.TotalVramBytes);
        }
        Assert.Equal(0, first.DeviceIndex);
        Assert.False(string.IsNullOrWhiteSpace(first.DeviceName));
    }

    [Fact]
    public void SupportsFp16_WhenAvailable_ReturnsExpectedSupport()
    {
        Assert.SkipWhen(!VulkanMemoryProvider.IsAvailable, NoVulkan);

        bool supportsFp16 = VulkanMemoryProvider.SupportsFp16(0);
        bool isCpu = VulkanMemoryProvider.IsCpuDevice(0);

        if (isCpu)
        {
            // A software rasterizer cannot run fp16 natively, whatever the feature bits say.
            Assert.False(supportsFp16);
        }

        // Nothing is asserted for a real GPU: fp16 is optional in Vulkan and every older
        // integrated part lacks it, so "supported" would be a claim about the hardware, not about
        // this code. What is pinned is that the answer is a bool and does not throw.
    }

    [Fact]
    public void ResolveGpuIndex_SkipsSoftwareRasterizers()
    {
        Assert.SkipWhen(!VulkanMemoryProvider.IsAvailable, NoVulkan);

        // vkEnumeratePhysicalDevices lists software rasterizers alongside the GPUs, while an
        // accelerator index counts only the GPUs the inference runtime can select. Walking only real
        // GPUs is what keeps the two apart, so the accelerator index can never land on a software
        // device — which with a lavapipe package installed is what it otherwise would do.
        List<int> resolved = new();
        for (int accelerator = 0; accelerator < VulkanMemoryProvider.DeviceCount; accelerator++)
        {
            int vulkan = VulkanMemoryProvider.ResolveGpuIndex(accelerator);
            Assert.True(vulkan >= 0, $"accelerator {accelerator} did not resolve");
            Assert.False(
                VulkanMemoryProvider.IsCpuDevice(accelerator),
                $"accelerator {accelerator} resolved to a software rasterizer"
            );
            resolved.Add(vulkan);
        }

        Assert.NotEmpty(resolved);
        Assert.Equal(resolved.Distinct().Count(), resolved.Count);
        Assert.Equal(resolved.OrderBy(index => index).ToList(), resolved);
        Assert.Equal(
            VulkanMemoryProvider.DeviceCount,
            VulkanMemoryProvider.QueryAllDevices().Count
        );
    }

    [Fact]
    public void QueryDevice_IsIndexedByAcceleratorIndex()
    {
        Assert.SkipWhen(!VulkanMemoryProvider.IsAvailable, NoVulkan);

        // Every caller hands the provider an accelerator index, so QueryDevice(i) has to be the
        // accelerator-th GPU, not the i-th physical device. QueryAllDevices is indexed the same way
        // and is the way to say what that meant.
        IReadOnlyList<VulkanGpuMemory> devices = VulkanMemoryProvider.QueryAllDevices();
        Assert.Equal(VulkanMemoryProvider.DeviceCount, devices.Count);

        for (int i = 0; i < devices.Count; i++)
        {
            Assert.Equal(devices[i].DeviceName, VulkanMemoryProvider.QueryDevice(i)?.DeviceName);
        }
    }
}
